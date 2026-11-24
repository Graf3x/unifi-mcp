using System.Text.Json.Serialization;

namespace UnifiMcp;

/// <summary>
/// A client as the OAuth flow sees it, whichever way it identified itself: a registration (its id) or a metadata
/// document (its URL). <see cref="ConsentKey"/> is what the owner's consent is filed under: the registered id, or a
/// hash of the document's URL.
/// </summary>
public sealed record Client(string ClientId, string ConsentKey, string DisplayName, IReadOnlyList<string> RedirectUris, bool FromMetadataDocument)
{
    internal static Client Registered(ClientRegistration registration) =>
        new(registration.ClientId, registration.ClientId, registration.ClientName, registration.RedirectUris, false);

    // The document is self-asserted: the relying party shown to the owner is the host that published it.
    internal static Client Documented(ClientMetadataDocument document) =>
        new(document.ClientId, ConsentKeyFor(document.ClientId), document.Host, document.RedirectUris, true);

    internal static string ConsentKeyFor(string clientId) =>
        HttpClientMetadataDocuments.IsClientIdUrl(clientId, out _) ? "cimd_" + TokenIssuer.Hash(clientId)[..32] : clientId;

    /// <summary>Whether a request may send the owner back to <paramref name="redirectUri"/>.</summary>
    public bool Allows(string redirectUri) => Clients.RedirectAllowed(RedirectUris, redirectUri);
}

/// <summary>What a client sends to register (RFC 7591): its client metadata.</summary>
public sealed class RegistrationRequest
{
    [JsonPropertyName("client_name")] public string? ClientName { get; set; }
    [JsonPropertyName("redirect_uris")] public List<string>? RedirectUris { get; set; }
    [JsonPropertyName("client_uri")] public string? ClientUri { get; set; }
    [JsonPropertyName("logo_uri")] public string? LogoUri { get; set; }
    [JsonPropertyName("software_id")] public string? SoftwareId { get; set; }
    [JsonPropertyName("software_version")] public string? SoftwareVersion { get; set; }
    [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; set; }
    [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; set; }
    [JsonPropertyName("response_types")] public List<string>? ResponseTypes { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
}

public abstract record RegistrationOutcome;

/// <summary>The client is registered: the stored registration, for the response.</summary>
public sealed record Registered(ClientRegistration Registration) : RegistrationOutcome;

/// <summary>Nothing was stored. <see cref="Error"/> is the RFC 7591 error code; <see cref="Description"/> is safe to send back.</summary>
public sealed record RegistrationRefused(string Error, string Description) : RegistrationOutcome;

/// <summary>
/// The Client module: which client a <c>client_id</c> names and whether it may be used. The OAuth endpoints translate
/// to and from HTTP; the grant ledger takes the <see cref="Client"/> this hands back.
/// </summary>
public sealed class Clients(SqliteOAuthStore store, IClientMetadataDocuments documents)
{
    internal const string Unknown = "Unknown client.";

    /// <summary>
    /// The client a <c>client_id</c> names, or null with a reason. A document address is fetched (and cached by the
    /// fetcher); anything else is looked up as a registration, and a disabled one is unknown.
    /// </summary>
    public async Task<(Client? Client, string? Problem)> ResolveAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(clientId)) return (null, "client_id is required.");
        if (HttpClientMetadataDocuments.IsClientIdUrl(clientId, out _))
        {
            // The fetcher checks before it caches; checked again here so any fetcher is held to the same rules.
            var (document, problem) = await documents.ResolveAsync(clientId, ct);
            problem ??= document is null ? null : DocumentProblem(document, clientId);
            return document is null || problem is not null ? (null, problem) : (Client.Documented(document), null);
        }
        var registered = await store.GetRegistrationAsync(clientId, ct);
        return registered is null || registered.Disabled ? (null, Unknown) : (Client.Registered(registered), null);
    }

    internal const int MaxRegistrationRedirectUris = 5;
    internal const int MaxRedirectUriLength = 2000;
    internal const int MaxClientNameLength = 200;
    private const int MaxProblemLength = 300;

    /// <summary>
    /// Registers a client (RFC 7591) under a fresh id, or says why not. Registration alone grants nothing: the owner
    /// still signs in and consents. <paramref name="registeredFrom"/> is only ever logged and stored.
    /// </summary>
    public async Task<RegistrationOutcome> RegisterAsync(RegistrationRequest request, string registeredFrom, CancellationToken ct = default)
    {
        if (request.ClientName is { Length: > MaxClientNameLength })
            return new RegistrationRefused("invalid_client_metadata", $"client_name must be at most {MaxClientNameLength} characters.");
        if (RegistrationProblem(request) is { } problem) return new RegistrationRefused("invalid_redirect_uri", problem);

        var registration = new ClientRegistration
        {
            ClientId = "mcp_" + TokenIssuer.NewSecret().Replace('-', 'a').Replace('_', 'b')[..24].ToLowerInvariant(),
            ClientName = Trim(request.ClientName, 100) ?? "Unnamed client",
            RedirectUris = request.RedirectUris!.Select(u => u.Trim()).Distinct(StringComparer.Ordinal).ToList(),
            ClientUri = Trim(request.ClientUri, 200),
            LogoUri = Trim(request.LogoUri, 400),
            SoftwareId = Trim(request.SoftwareId, 100),
            SoftwareVersion = Trim(request.SoftwareVersion, 50),
            RegisteredFrom = registeredFrom,
        };
        return new Registered(await store.CreateRegistrationAsync(registration, ct));
    }

    /// <summary>Marks a registration used, when tokens were issued to it. A document client has no record to mark.</summary>
    public Task RecordUseAsync(Client client, CancellationToken ct = default) =>
        client.FromMetadataDocument ? Task.CompletedTask : store.TouchRegistrationAsync(client.ClientId, ct);

    // ---- redirect addresses: the rules for both kinds of client ----------------------------------------------------

    /// <summary>
    /// What is wrong with a registration's redirects and client type, or null. Anyone may register, so a registered
    /// redirect must be https; loopback is for document clients, whose publisher vouches for them.
    /// </summary>
    private static string? RegistrationProblem(RegistrationRequest request)
    {
        if (request.RedirectUris is not { Count: > 0 }) return "redirect_uris is required.";
        if (request.RedirectUris.Count > MaxRegistrationRedirectUris) return $"At most {MaxRegistrationRedirectUris} redirect_uris.";
        foreach (var raw in request.RedirectUris)
        {
            if (raw is { Length: > MaxRedirectUriLength }) return $"Each redirect_uri must be at most {MaxRedirectUriLength} characters.";
            if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri)) return $"redirect_uri '{Truncate(raw)}' is not an absolute URI.";
            if (!string.IsNullOrEmpty(uri.Fragment)) return "redirect_uris must not contain a fragment.";
            if (uri.Scheme != Uri.UriSchemeHttps) return $"redirect_uri '{Truncate(raw)}' must use https.";
        }
        if (!IsPublic(request.TokenEndpointAuthMethod)) return "Only public clients (token_endpoint_auth_method=none, PKCE) are supported.";
        if (request.GrantTypes is { Count: > 0 } && request.GrantTypes.Any(g => g is not ("authorization_code" or "refresh_token")))
            return "Supported grant_types: authorization_code, refresh_token.";
        if (request.ResponseTypes is { Count: > 0 } && request.ResponseTypes.Any(r => r != "code")) return "Supported response_types: code.";
        return null;
    }

    internal const int MaxDocumentRedirectUris = 10;

    /// <summary>
    /// What is wrong with a metadata document's redirects and client type, or null. Every redirect must be same-origin
    /// with the document's URL or a loopback address (native clients such as Claude Code bind an ephemeral port);
    /// anything else lets a document send the owner to a host its publisher does not control.
    /// </summary>
    internal static string? DocumentProblem(ClientMetadataDocument document, string clientIdUrl)
    {
        if (document.RedirectUris.Count is 0 or > MaxDocumentRedirectUris) return $"client metadata document must list 1–{MaxDocumentRedirectUris} redirect_uris.";
        if (!IsPublic(document.TokenEndpointAuthMethod)) return "only public clients (token_endpoint_auth_method=none) are supported.";

        var origin = new Uri(clientIdUrl);
        foreach (var raw in document.RedirectUris)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var r) || !string.IsNullOrEmpty(r.Fragment)) return $"redirect_uri '{raw}' is not a valid absolute URI.";
            if (IsLoopback(r)) continue;
            if (r.Scheme != origin.Scheme || !string.Equals(r.Host, origin.Host, StringComparison.OrdinalIgnoreCase) || r.Port != origin.Port)
                return $"redirect_uri '{raw}' is not same-origin with the client_id URL.";
        }
        return null;
    }

    /// <summary>A public client: no secret, PKCE instead. The only kind this server serves.</summary>
    private static bool IsPublic(string? tokenEndpointAuthMethod) => tokenEndpointAuthMethod is null or "none";

    /// <summary>A redirect to a program on the owner's own computer: plain http to a loopback address or localhost.</summary>
    internal static bool IsLoopback(Uri u) =>
        u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Exact match for web redirects; port-agnostic for loopback ones (RFC 8252 §7.3, applied to localhost too
    /// because Claude Code declares it).
    /// </summary>
    internal static bool RedirectAllowed(IEnumerable<string> registered, string requested)
    {
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var req)) return false;
        foreach (var raw in registered)
        {
            if (string.Equals(raw, requested, StringComparison.Ordinal)) return true;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var reg)) continue;
            if (IsLoopback(reg) && IsLoopback(req) &&
                string.Equals(reg.Host, req.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reg.AbsolutePath, req.AbsolutePath, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    /// <summary>A problem can echo the client's own text: only this much of it.</summary>
    private static string? Truncate(string? s) => s is null || s.Length <= MaxProblemLength ? s : s[..MaxProblemLength] + "…";
}
