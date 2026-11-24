using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiMcp;

/// <summary>
/// Client ID Metadata Documents (draft-ietf-oauth-client-id-metadata-document, adopted by MCP 2025-11): the
/// <c>client_id</c> is an HTTPS URL that dereferences to the client's own registration metadata. Claude's hosted
/// surfaces and Claude Code identify themselves this way, so no registration endpoint round-trip and no stored
/// client per connection. The document is self-asserted, so it is validated (self-referential, sane redirect
/// URIs, by the Client module) and the consent page names the URL's host as the relying party, never the document's client_name.
/// </summary>
public interface IClientMetadataDocuments
{
    /// <summary>The validated document for a client_id URL, or null with a reason when it cannot be used.</summary>
    Task<(ClientMetadataDocument? Document, string? Problem)> ResolveAsync(string clientIdUrl, CancellationToken ct = default);
}

public sealed class ClientMetadataDocument
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = string.Empty;
    [JsonPropertyName("client_name")] public string? ClientName { get; set; }
    [JsonPropertyName("client_uri")] public string? ClientUri { get; set; }
    [JsonPropertyName("logo_uri")] public string? LogoUri { get; set; }
    [JsonPropertyName("redirect_uris")] public List<string> RedirectUris { get; set; } = [];
    [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; set; }
    [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; set; }

    /// <summary>The relying party as shown to the user: the host that published the document.</summary>
    public string Host => Uri.TryCreate(ClientId, UriKind.Absolute, out var u) ? u.Host : ClientId;
}

public sealed class HttpClientMetadataDocuments(IHttpClientFactory httpClientFactory, ILogger<HttpClientMetadataDocuments> logger, TimeProvider time) : IClientMetadataDocuments
{
    public const string HttpClientName = "ClientMetadataDocuments";
    internal static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    internal const int MaxDocumentBytes = 64 * 1024;
    internal const int MaxCacheEntries = 256;
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Who is fetching, for the publisher's logs. A constant: never a version or host detail beyond this.</summary>
    internal const string UserAgent = "unifi-mcp/0.2";

    private readonly ConcurrentDictionary<string, (ClientMetadataDocument Doc, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);

    public async Task<(ClientMetadataDocument? Document, string? Problem)> ResolveAsync(string clientIdUrl, CancellationToken ct = default)
    {
        if (!IsClientIdUrl(clientIdUrl, out var uri)) return (null, "client_id must be an https URL with a path and no fragment.");

        if (_cache.TryGetValue(clientIdUrl, out var cached) && cached.Until > time.GetUtcNow()) return (cached.Doc, null);

        // One deadline for the whole fetch (connect, headers and body). HttpClient.Timeout stops at the headers with
        // ResponseHeadersRead, so a host that trickles the body would otherwise hold the request open.
        using var deadline = new CancellationTokenSource(FetchTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return (null, $"client metadata document returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxDocumentBytes) return (null, "client metadata document is too large.");

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var limited = await ReadBoundedAsync(stream, MaxDocumentBytes, token);
            if (limited is null) return (null, "client metadata document is too large.");

            var doc = await JsonSerializer.DeserializeAsync<ClientMetadataDocument>(limited, cancellationToken: token);
            var problem = Validate(doc, clientIdUrl);
            if (problem is not null) return (null, problem);

            Remember(clientIdUrl, doc!);
            return (doc, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return (null, "client metadata document timed out."); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OAuth: could not fetch client metadata document {Url}", LogText.Clean(clientIdUrl));
            return (null, "client metadata document could not be fetched.");
        }
    }

    /// <summary>
    /// Reads at most <paramref name="max"/> bytes without trusting Content-Length (a chunked body has none), and
    /// gives up as soon as the body proves larger. Null means too large.
    /// </summary>
    internal static async Task<MemoryStream?> ReadBoundedAsync(Stream stream, int max, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) break;
            if (buffer.Length + n > max) return null;
            buffer.Write(chunk, 0, n);
        }
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>
    /// Anonymous callers choose these URLs, so the cache is bounded: expired entries go first, then the oldest.
    /// </summary>
    private void Remember(string url, ClientMetadataDocument doc)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            var now = time.GetUtcNow();
            foreach (var expired in _cache.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList()) _cache.TryRemove(expired, out _);
            while (_cache.Count >= MaxCacheEntries)
            {
                var oldest = _cache.OrderBy(kv => kv.Value.Until).Select(kv => kv.Key).FirstOrDefault();
                if (oldest is null || !_cache.TryRemove(oldest, out _)) break;
            }
        }
        _cache[url] = (doc, time.GetUtcNow().Add(CacheFor));
    }

    /// <summary>
    /// The fetch is triggered by an anonymous client_id, so the handler resolves the host itself, refuses every
    /// non-public address (private, link-local, loopback, IMDS, multicast) and connects to the exact address it
    /// checked. Resolving inside the connect step is what defeats DNS rebinding between check and use.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var publicAddress = addresses.FirstOrDefault(IsPublicAddress)
                ?? throw new HttpRequestException($"client metadata host '{host}' does not resolve to a public address.");
            var socket = new Socket(publicAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(publicAddress, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    internal static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal || ip.IsIPv6Teredo);
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,                              // this-network, private, loopback
            100 when b[1] >= 64 && b[1] <= 127 => false,          // carrier-grade NAT
            169 when b[1] == 254 => false,                        // link-local incl. 169.254.169.254 (IMDS)
            172 when b[1] >= 16 && b[1] <= 31 => false,           // private
            192 when b[1] == 168 => false,                        // private
            192 when b[1] == 0 && b[2] is 0 or 2 => false,        // IETF protocol assignments, TEST-NET-1
            198 when b[1] is 18 or 19 => false,                   // benchmarking
            198 when b[1] == 51 && b[2] == 100 => false,          // TEST-NET-2
            203 when b[1] == 0 && b[2] == 113 => false,           // TEST-NET-3
            >= 224 => false,                                      // multicast, reserved
            _ => true,
        };
    }

    internal static bool IsClientIdUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        // https, a DNS name (never an IP literal), a path (so the origin's root document cannot be claimed), no
        // fragment, no credentials. The connect step separately refuses names that resolve to non-public addresses.
        if (parsed.Scheme != Uri.UriSchemeHttps || parsed.IsLoopback || string.IsNullOrEmpty(parsed.Host) || !parsed.Host.Contains('.')) return false;
        if (parsed.HostNameType != UriHostNameType.Dns) return false;
        if (parsed.AbsolutePath is "/" or "" || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// The document must name itself, and pass the Client module's rules (redirects, public client). Checked before
    /// it is cached, so a refused document is fetched again next time.
    /// </summary>
    internal static string? Validate(ClientMetadataDocument? doc, string clientIdUrl)
    {
        if (doc is null) return "client metadata document is empty.";
        if (!string.Equals(doc.ClientId, clientIdUrl, StringComparison.Ordinal)) return "client metadata document is not self-referential (client_id differs from its URL).";
        return Clients.DocumentProblem(doc, clientIdUrl);
    }
}
