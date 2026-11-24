using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

/// <summary>The Client module, straight through its interface: a real SQLite file and a fake document fetcher. No HTTP.</summary>
public sealed class OAuthClientsTests : IDisposable
{
    private const string DocumentUrl = "https://claude.ai/oauth/claude-code-client-metadata";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"clients-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteOAuthStore _store;
    private readonly FakeDocuments _documents = new();

    public OAuthClientsTests() => _store = new SqliteOAuthStore(_path, _time);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            File.Delete(f);
    }

    /// <summary>Hands back whatever document (or problem) the test set, and counts the asks.</summary>
    private sealed class FakeDocuments : IClientMetadataDocuments
    {
        public ClientMetadataDocument? Document;
        public string? Problem = "client metadata document could not be fetched.";
        public int Calls;

        public Task<(ClientMetadataDocument? Document, string? Problem)> ResolveAsync(string clientIdUrl, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Document is null ? ((ClientMetadataDocument?)null, Problem) : (Document, (string?)null));
        }
    }

    private Clients Clients() => new(_store, _documents);

    private static ClientMetadataDocument Document(params string[] redirects) =>
        new() { ClientId = DocumentUrl, ClientName = "Claude Code", RedirectUris = [.. redirects] };

    // ---- resolving ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_registered_client_resolves_by_its_id_and_is_consented_to_by_it()
    {
        await _store.CreateClientAsync(new OAuthClient { ClientId = "mcp_abc", ClientName = "Claude", RedirectUris = ["https://claude.ai/cb"] });

        var (client, problem) = await Clients().ResolveAsync("mcp_abc");

        Assert.Null(problem);
        Assert.Equal(new Client("mcp_abc", "mcp_abc", "Claude", client!.RedirectUris, false), client);
        Assert.Equal(["https://claude.ai/cb"], client.RedirectUris);
        Assert.Equal(0, _documents.Calls);
    }

    [Fact]
    public async Task An_unknown_or_disabled_registration_is_an_unknown_client()
    {
        await _store.CreateClientAsync(new OAuthClient { ClientId = "mcp_off", ClientName = "Off", RedirectUris = ["https://claude.ai/cb"], Disabled = true });

        Assert.Equal((null, "Unknown client."), await Clients().ResolveAsync("mcp_nope"));
        Assert.Equal((null, "Unknown client."), await Clients().ResolveAsync("mcp_off"));
    }

    [Fact]
    public async Task A_missing_client_id_is_refused()
    {
        Assert.Equal((null, "client_id is required."), await Clients().ResolveAsync(""));
        Assert.Equal(0, _documents.Calls);
    }

    [Fact]
    public async Task A_document_client_is_named_by_its_host_and_consented_to_by_a_hash_of_its_url()
    {
        _documents.Document = Document("http://127.0.0.1/callback");

        var (client, problem) = await Clients().ResolveAsync(DocumentUrl);

        Assert.Null(problem);
        Assert.Equal(DocumentUrl, client!.ClientId);
        Assert.Equal("claude.ai", client.DisplayName); // the document is self-asserted: its client_name is never shown
        Assert.True(client.FromMetadataDocument);
        Assert.Equal("cimd_" + TokenIssuer.Hash(DocumentUrl)[..32], client.ConsentKey);
        Assert.Equal(Client.ConsentKeyFor(DocumentUrl), client.ConsentKey);
    }

    [Fact]
    public async Task A_document_that_cannot_be_used_passes_its_problem_on()
    {
        _documents.Problem = "client metadata document returned HTTP 404.";

        Assert.Equal((null, "client metadata document returned HTTP 404."), await Clients().ResolveAsync(DocumentUrl));
    }

    [Fact]
    public async Task A_url_that_is_not_a_document_address_is_looked_up_as_a_registration()
    {
        Assert.Equal((null, "Unknown client."), await Clients().ResolveAsync("http://claude.ai/x"));
        Assert.Equal(0, _documents.Calls);
    }

    // ---- redirect addresses -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", null)]
    [InlineData("https://claude.ai:443/cb", null)]
    [InlineData("http://127.0.0.1:1234/cb", null)]
    [InlineData("http://localhost/cb", null)]
    [InlineData("https://claude.ai:8443/cb", "redirect_uri 'https://claude.ai:8443/cb' is not same-origin with the client_id URL.")]
    [InlineData("https://evil.example/cb", "redirect_uri 'https://evil.example/cb' is not same-origin with the client_id URL.")]
    [InlineData("http://claude.ai/cb", "redirect_uri 'http://claude.ai/cb' is not same-origin with the client_id URL.")]
    [InlineData("https://claude.ai/cb#here", "redirect_uri 'https://claude.ai/cb#here' is not a valid absolute URI.")]
    [InlineData("not a uri", "redirect_uri 'not a uri' is not a valid absolute URI.")]
    public async Task A_document_s_redirects_must_share_its_origin_or_be_loopback(string redirect, string? problem)
    {
        _documents.Document = Document(redirect);

        var (client, refused) = await Clients().ResolveAsync(DocumentUrl);

        Assert.Equal(problem, refused);
        Assert.Equal(problem is null, client is not null);
    }

    [Fact]
    public async Task A_document_lists_one_to_ten_redirects()
    {
        const string tooFewOrMany = "client metadata document must list 1–10 redirect_uris.";
        _documents.Document = Document();
        Assert.Equal((null, tooFewOrMany), await Clients().ResolveAsync(DocumentUrl));
        _documents.Document = Document([.. Enumerable.Range(0, 11).Select(i => $"https://claude.ai/cb{i}")]);
        Assert.Equal((null, tooFewOrMany), await Clients().ResolveAsync(DocumentUrl));
        _documents.Document = Document([.. Enumerable.Range(0, 10).Select(i => $"https://claude.ai/cb{i}")]);
        Assert.NotNull((await Clients().ResolveAsync(DocumentUrl)).Client);
    }

    [Fact]
    public async Task A_document_client_must_be_public()
    {
        _documents.Document = Document("https://claude.ai/cb");
        _documents.Document.TokenEndpointAuthMethod = "client_secret_basic";

        Assert.Equal((null, "only public clients (token_endpoint_auth_method=none) are supported."), await Clients().ResolveAsync(DocumentUrl));
    }

    [Fact]
    public void A_loopback_redirect_matches_on_any_port_and_a_web_one_exactly()
    {
        var loopback = new Client(DocumentUrl, Client.ConsentKeyFor(DocumentUrl), "claude.ai", ["http://127.0.0.1/cb"], true);
        Assert.True(loopback.Allows("http://127.0.0.1:5555/cb"));
        Assert.False(loopback.Allows("http://127.0.0.1:5555/other"));

        var web = new Client("mcp_abc", "mcp_abc", "Claude", ["https://claude.ai/cb"], false);
        Assert.True(web.Allows("https://claude.ai/cb"));
        Assert.False(web.Allows("https://claude.ai:444/cb"));
        Assert.False(web.Allows(""));
    }

    // ---- last use -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recording_use_marks_a_registration_and_leaves_a_document_client_alone()
    {
        await _store.CreateClientAsync(new OAuthClient { ClientId = "mcp_abc", ClientName = "Claude", RedirectUris = ["https://claude.ai/cb"] });
        var registered = (await Clients().ResolveAsync("mcp_abc")).Client!;
        _time.Advance(TimeSpan.FromHours(2));

        await Clients().RecordUseAsync(registered);
        await Clients().RecordUseAsync(new Client(DocumentUrl, Client.ConsentKeyFor(DocumentUrl), "claude.ai", ["https://claude.ai/cb"], true));

        Assert.Equal(_time.GetUtcNow(), (await _store.GetClientAsync("mcp_abc"))!.LastUsedAt);
    }
}
