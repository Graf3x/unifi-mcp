namespace UnifiMcp;

/// <summary>
/// A client as the OAuth flow sees it, whichever way it identified itself: a registration (its id) or a metadata
/// document (its URL). <see cref="ConsentKey"/> is what the owner's consent is filed under: the registered id, or a
/// hash of the document's URL.
/// </summary>
public sealed record Client(string ClientId, string ConsentKey, string DisplayName, IReadOnlyList<string> RedirectUris, bool FromMetadataDocument)
{
    internal static Client Registered(OAuthClient registration) =>
        new(registration.ClientId, registration.ClientId, registration.ClientName, registration.RedirectUris, false);

    // The document is self-asserted: the relying party shown to the owner is the host that published it.
    internal static Client Documented(ClientMetadataDocument document) =>
        new(document.ClientId, ConsentKeyFor(document.ClientId), document.Host, document.RedirectUris, true);

    internal static string ConsentKeyFor(string clientId) =>
        HttpClientMetadataDocuments.IsClientIdUrl(clientId, out _) ? "cimd_" + TokenIssuer.Hash(clientId)[..32] : clientId;

    /// <summary>Whether a request may send the owner back to <paramref name="redirectUri"/>.</summary>
    public bool Allows(string redirectUri) => Clients.RedirectAllowed(RedirectUris, redirectUri);
}

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
        var registered = await store.GetClientAsync(clientId, ct);
        return registered is null || registered.Disabled ? (null, Unknown) : (Client.Registered(registered), null);
    }

    /// <summary>Marks a registration used, when tokens were issued to it. A document client has no record to mark.</summary>
    public Task RecordUseAsync(Client client, CancellationToken ct = default) =>
        client.FromMetadataDocument ? Task.CompletedTask : store.TouchClientAsync(client.ClientId, ct);

    // ---- redirect addresses: the rules for both kinds of client ----------------------------------------------------

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
}
