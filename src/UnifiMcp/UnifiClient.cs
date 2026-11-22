using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace UnifiMcp;

/// <summary>The call could not be made or the console refused it. The message is safe to show the caller.</summary>
public sealed class UnifiError(string message) : Exception(message);

/// <summary>The owner has not supplied the API key or certificate pin yet.</summary>
public sealed class UnifiNotConfigured(string message) : Exception(message);

public sealed record UnifiResponse(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>A site on the console: its id (the Integration API's name for it), its internal name (the Internal API's) and its display name.</summary>
public sealed record Site(string Id, string? Reference, string Name);

/// <summary>A certificate the console presented and the server refused: its fingerprints and, in CA mode, why.</summary>
public sealed record RejectedCertificate(string Certificate, string Key, SslPolicyErrors Errors = SslPolicyErrors.None);

/// <summary>The last certificate refused, for a useful error.</summary>
public sealed class PinState
{
    public RejectedCertificate? Rejected { get; set; }
}

/// <summary>
/// TLS to the console. Its certificate is usually self-signed, so by default it is pinned: a pin is the SHA-256 of the
/// certificate's DER or of its public key (SubjectPublicKeyInfo, which survives a re-issued certificate that keeps its
/// key), and several may be listed. With no pin nothing matches, so verification cannot be off. With
/// UNIFI_CERT_TRUST=ca the usual chain and host-name checks apply instead. Redirects are never followed, so the key
/// cannot be sent elsewhere.
/// </summary>
public static class CertPin
{
    public static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    public static string KeyFingerprint(X509Certificate cert)
    {
        if (cert is X509Certificate2 c2) return Convert.ToHexString(SHA256.HashData(c2.PublicKey.ExportSubjectPublicKeyInfo()));
        using var copy = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
        return Convert.ToHexString(SHA256.HashData(copy.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public static bool Matches(X509Certificate? cert, IReadOnlyCollection<string>? pins)
    {
        if (cert is null || pins is not { Count: > 0 }) return false;
        var certPin = Fingerprint(cert);
        var keyPin = KeyFingerprint(cert);
        return pins.Any(p => string.Equals(p, certPin, StringComparison.OrdinalIgnoreCase) || string.Equals(p, keyPin, StringComparison.OrdinalIgnoreCase));
    }

    public static SocketsHttpHandler CreateHandler(Func<IReadOnlyCollection<string>?> pins, CertTrust trust, PinState state) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                var ok = trust == CertTrust.Ca ? cert is not null && errors == SslPolicyErrors.None : Matches(cert, pins());
                if (!ok && cert is not null) state.Rejected = new(Fingerprint(cert), KeyFingerprint(cert), errors);
                return ok;
            },
        },
    };
}

/// <summary>
/// The console's two APIs over one <see cref="ConsoleTransport"/>. Non-GET requests are refused here as well as by tool
/// registration while writes are off. The console's internal API (the one its own UI uses) is read only through
/// <see cref="ConsoleReads"/>.
/// </summary>
public sealed class UnifiClient(Settings settings, ConsoleTransport transport)
{
    private const string ApiRoot = "/proxy/network/integration";
    private const string ConsoleRoot = "/proxy/network";
    private const int PageSize = 200;

    private readonly ConcurrentDictionary<string, Site> _sites = new(StringComparer.OrdinalIgnoreCase);

    public Settings Settings => settings;

    public Task<UnifiResponse> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (method != HttpMethod.Get && !settings.AllowWrites)
            throw new UnifiError("Writes are disabled (UNIFI_ALLOW_WRITES is not true), so nothing was sent.");
        return transport.SendAsync(method, ApiRoot + path, body, ct);
    }

    /// <summary>A request to the internal API, relative to /proxy/network. Only <see cref="ConsoleReads"/> sends these.</summary>
    internal Task<UnifiResponse> SendInternalAsync(HttpMethod method, string path, string? body, CancellationToken ct) =>
        transport.SendAsync(method, ConsoleRoot + path, body, ct);

    public async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, path, null, ct);
        if (!r.Ok) throw new UnifiError($"The console answered {r.Status} to GET {path.Split('?')[0]}: {Short(r.Body)}");
        try { return JsonDocument.Parse(r.Body).RootElement; }
        catch (JsonException) { throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not JSON."); }
    }

    /// <summary>Every item of a paged list.</summary>
    public async Task<List<JsonElement>> GetAllAsync(string path, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var sep = path.Contains('?') ? '&' : '?';
        for (var offset = 0; ;)
        {
            var page = await GetJsonAsync($"{path}{sep}offset={offset}&limit={PageSize}", ct);
            try
            {
                var data = page.GetProperty("data").EnumerateArray().ToList();
                all.AddRange(data);
                offset += data.Count;
                if (data.Count == 0 || offset >= page.GetProperty("totalCount").GetInt64()) return all;
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not the paged list this server expects.");
            }
        }
    }

    /// <summary>
    /// A site by its id, name or internal name, ignoring case; omitted means the configured site. One lookup per hint,
    /// then cached: the console's sites do not change under a running server often enough to look again.
    /// </summary>
    public async Task<Site> FindSiteAsync(string? hint, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(hint) ? settings.Site : hint.Trim();
        if (_sites.TryGetValue(wanted, out var known)) return known;

        var sites = (await GetAllAsync("/v1/sites", ct)).Where(s => Str(s, "id") is { Length: > 0 }).ToList();
        // An id names one site, so it wins over another site whose name or internal name happens to be that id.
        bool Is(JsonElement s, string field) => string.Equals(Str(s, field), wanted, StringComparison.OrdinalIgnoreCase);
        if (sites.Where(s => Is(s, "id")).Concat(sites.Where(s => Is(s, "name") || Is(s, "internalReference"))).FirstOrDefault() is { ValueKind: JsonValueKind.Object } found)
            return _sites[wanted] = new Site(Str(found, "id")!, Str(found, "internalReference"), Str(found, "name") ?? Str(found, "id")!);
        throw new UnifiError($"The console has no site '{wanted}'. Sites: {string.Join(", ", sites.Select(s => Str(s, "name")))}.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string text) => text.Length <= 500 ? text : text[..500] + "...";
}
