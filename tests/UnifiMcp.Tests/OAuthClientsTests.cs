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
        await _store.CreateRegistrationAsync(new ClientRegistration { ClientId = "mcp_abc", ClientName = "Claude", RedirectUris = ["https://claude.ai/cb"] });

        var (client, problem) = await Clients().ResolveAsync("mcp_abc");

        Assert.Null(problem);
        Assert.Equal(new Client("mcp_abc", "mcp_abc", "Claude", client!.RedirectUris, false), client);
        Assert.Equal(["https://claude.ai/cb"], client.RedirectUris);
        Assert.Equal(0, _documents.Calls);
    }

    [Fact]
    public async Task An_unknown_or_disabled_registration_is_an_unknown_client()
    {
        await _store.CreateRegistrationAsync(new ClientRegistration { ClientId = "mcp_off", ClientName = "Off", RedirectUris = ["https://claude.ai/cb"], Disabled = true });

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

    // ---- registering ----------------------------------------------------------------------------------------------

    private static RegistrationRequest Asking(params string[] redirects) => new() { ClientName = "Claude", RedirectUris = [.. redirects] };

    private async Task<RegistrationRefused> RefusedAsync(RegistrationRequest request) =>
        Assert.IsType<RegistrationRefused>(await Clients().RegisterAsync(request, "203.0.113.7 test"));

    [Fact]
    public async Task Registering_stores_a_public_client_under_a_fresh_id_and_it_resolves()
    {
        var request = new RegistrationRequest
        {
            ClientName = "  Claude  ", RedirectUris = [" https://claude.ai/cb ", "https://claude.ai/cb", "https://claude.ai/other"],
            ClientUri = "https://claude.ai", SoftwareId = new string('s', 150), TokenEndpointAuthMethod = "none",
            GrantTypes = ["authorization_code", "refresh_token"], ResponseTypes = ["code"],
        };

        var registered = Assert.IsType<Registered>(await Clients().RegisterAsync(request, "203.0.113.7 test")).Registration;

        Assert.Matches("^mcp_[a-z0-9]{24}$", registered.ClientId);
        Assert.Equal("Claude", registered.ClientName);
        Assert.Equal(["https://claude.ai/cb", "https://claude.ai/other"], registered.RedirectUris); // trimmed, each once
        Assert.Equal(100, registered.SoftwareId!.Length);
        Assert.Equal("203.0.113.7 test", registered.RegisteredFrom);
        Assert.Equal(_time.GetUtcNow(), registered.CreatedAt);

        var client = (await Clients().ResolveAsync(registered.ClientId)).Client!;
        Assert.Equal(("Claude", registered.ClientId, false), (client.DisplayName, client.ConsentKey, client.FromMetadataDocument));
        Assert.True(client.Allows("https://claude.ai/other"));
    }

    [Fact]
    public async Task Each_registration_gets_its_own_id_and_an_unnamed_one_a_placeholder_name()
    {
        var first = Assert.IsType<Registered>(await Clients().RegisterAsync(new RegistrationRequest { RedirectUris = ["https://claude.ai/cb"] }, "x")).Registration;
        var second = Assert.IsType<Registered>(await Clients().RegisterAsync(Asking("https://claude.ai/cb"), "x")).Registration;

        Assert.NotEqual(first.ClientId, second.ClientId);
        Assert.Equal("Unnamed client", first.ClientName);
    }

    [Theory]
    [InlineData("http://claude.ai/cb", "redirect_uri 'http://claude.ai/cb' must use https.")]
    [InlineData("http://127.0.0.1/cb", "redirect_uri 'http://127.0.0.1/cb' must use https.")] // loopback is for document clients only
    [InlineData("https://claude.ai/cb#here", "redirect_uris must not contain a fragment.")]
    [InlineData("not a uri", "redirect_uri 'not a uri' is not an absolute URI.")]
    public async Task A_registered_redirect_must_be_an_https_address(string redirect, string problem) =>
        Assert.Equal(new RegistrationRefused("invalid_redirect_uri", problem), await RefusedAsync(Asking(redirect)));

    [Fact]
    public async Task A_registration_lists_one_to_five_redirects_of_up_to_2000_characters()
    {
        Assert.Equal("redirect_uris is required.", (await RefusedAsync(new RegistrationRequest())).Description);
        Assert.Equal("redirect_uris is required.", (await RefusedAsync(Asking())).Description);
        Assert.Equal("At most 5 redirect_uris.", (await RefusedAsync(Asking([.. Enumerable.Range(0, 6).Select(i => $"https://claude.ai/cb{i}")]))).Description);
        Assert.IsType<Registered>(await Clients().RegisterAsync(Asking([.. Enumerable.Range(0, 5).Select(i => $"https://claude.ai/cb{i}")]), "x"));

        var longest = "https://claude.ai/" + new string('a', 2000 - "https://claude.ai/".Length);
        Assert.IsType<Registered>(await Clients().RegisterAsync(Asking(longest), "x"));
        Assert.Equal(new RegistrationRefused("invalid_redirect_uri", "Each redirect_uri must be at most 2000 characters."), await RefusedAsync(Asking(longest + "a")));
    }

    [Fact]
    public async Task A_client_name_is_at_most_200_characters()
    {
        var named = Asking("https://claude.ai/cb");
        named.ClientName = new string('n', 200);
        Assert.IsType<Registered>(await Clients().RegisterAsync(named, "x"));

        named.ClientName = new string('n', 201);
        Assert.Equal(new RegistrationRefused("invalid_client_metadata", "client_name must be at most 200 characters."), await RefusedAsync(named));
    }

    [Fact]
    public async Task A_registered_client_must_be_public_and_use_the_code_flow()
    {
        var secret = Asking("https://claude.ai/cb");
        secret.TokenEndpointAuthMethod = "client_secret_basic";
        var credentials = Asking("https://claude.ai/cb");
        credentials.GrantTypes = ["client_credentials"];
        var implicitFlow = Asking("https://claude.ai/cb");
        implicitFlow.ResponseTypes = ["token"];

        Assert.Equal(new RegistrationRefused("invalid_redirect_uri", "Only public clients (token_endpoint_auth_method=none, PKCE) are supported."), await RefusedAsync(secret));
        Assert.Equal(new RegistrationRefused("invalid_redirect_uri", "Supported grant_types: authorization_code, refresh_token."), await RefusedAsync(credentials));
        Assert.Equal(new RegistrationRefused("invalid_redirect_uri", "Supported response_types: code."), await RefusedAsync(implicitFlow));
    }

    // ---- last use -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recording_use_marks_a_registration_and_leaves_a_document_client_alone()
    {
        await _store.CreateRegistrationAsync(new ClientRegistration { ClientId = "mcp_abc", ClientName = "Claude", RedirectUris = ["https://claude.ai/cb"] });
        var registered = (await Clients().ResolveAsync("mcp_abc")).Client!;
        _time.Advance(TimeSpan.FromHours(2));

        await Clients().RecordUseAsync(registered);
        await Clients().RecordUseAsync(new Client(DocumentUrl, Client.ConsentKeyFor(DocumentUrl), "claude.ai", ["https://claude.ai/cb"], true));

        Assert.Equal(_time.GetUtcNow(), (await _store.GetRegistrationAsync("mcp_abc"))!.LastUsedAt);
    }
}
