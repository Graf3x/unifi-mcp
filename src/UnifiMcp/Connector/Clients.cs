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
    public bool Allows(string redirectUri) => HttpClientMetadataDocuments.RedirectAllowed(RedirectUris, redirectUri);
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
            var (document, problem) = await documents.ResolveAsync(clientId, ct);
            return document is null ? (null, problem) : (Client.Documented(document), null);
        }
        var registered = await store.GetClientAsync(clientId, ct);
        return registered is null || registered.Disabled ? (null, Unknown) : (Client.Registered(registered), null);
    }

    /// <summary>Marks a registration used, when tokens were issued to it. A document client has no record to mark.</summary>
    public Task RecordUseAsync(Client client, CancellationToken ct = default) =>
        client.FromMetadataDocument ? Task.CompletedTask : store.TouchClientAsync(client.ClientId, ct);
}
