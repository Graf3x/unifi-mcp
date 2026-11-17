using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace UnifiMcp.Tests;

/// <summary>MCP_ACCESS_TOKEN: a fixed credential for /mcp on the public side, for clients that send a static header.</summary>
public sealed class AccessTokenTests
{
    private const string Token = "test-access-token-0123456789abcdef0123";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    private static async Task<HttpStatusCode> InitializeAsync(PublicHarness h, Action<HttpRequestHeaders> headers, HttpClient? client = null)
    {
        using var c = client ?? h.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        headers(request.Headers);
        return (await c.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task The_token_as_a_bearer_reaches_mcp()
    {
        await using var h = await PublicHarness.StartAsync(accessToken: Token);
        Assert.Equal(HttpStatusCode.OK, await OAuthFlowTests.InitializeAsync(h, Token));
    }

    [Fact]
    public async Task The_token_in_x_api_key_reaches_mcp()
    {
        await using var h = await PublicHarness.StartAsync(accessToken: Token);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(h, x => x.Add("X-Api-Key", Token)));
    }

    [Fact]
    public async Task It_works_with_code_sign_in_and_no_google_values()
    {
        await using var h = await PublicHarness.StartAsync(signIn: SignInMode.Code, accessToken: Token);
        Assert.Equal(HttpStatusCode.OK, await OAuthFlowTests.InitializeAsync(h, Token));
    }

    [Theory]
    [InlineData("test-access-token-0123456789abcdef012")]   // one character short
    [InlineData("test-access-token-0123456789abcdef01234")] // one character long
    [InlineData("TEST-ACCESS-TOKEN-0123456789ABCDEF0123")]  // case matters
    public async Task A_wrong_token_is_401(string presented)
    {
        await using var h = await PublicHarness.StartAsync(accessToken: Token);
        Assert.Equal(HttpStatusCode.Unauthorized, await OAuthFlowTests.InitializeAsync(h, presented));
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(h, x => x.Add("X-Api-Key", presented)));
    }

    [Fact]
    public async Task Without_a_configured_token_nothing_static_is_accepted()
    {
        await using var h = await PublicHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await OAuthFlowTests.InitializeAsync(h, Token));
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(h, x => x.Add("X-Api-Key", Token)));
    }

    [Fact]
    public async Task OAuth_tokens_still_work_next_to_it()
    {
        await using var h = await PublicHarness.StartAsync(accessToken: Token);
        var clientId = await OAuthFlowTests.RegisterAsync(h);
        var tokens = await OAuthFlowTests.ExchangeAsync(h, clientId, await OAuthFlowTests.AuthorizeAsync(h, clientId));
        Assert.Equal(HttpStatusCode.OK, await OAuthFlowTests.InitializeAsync(h, tokens.Access));
    }

    [Fact]
    public async Task The_token_never_opens_the_owner_pages()
    {
        await using var h = await PublicHarness.StartAsync(accessToken: Token);
        using var client = h.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        client.DefaultRequestHeaders.Add("X-Api-Key", Token);
        var r = await client.GetAsync("/connections");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task The_token_is_not_accepted_on_the_tailnet()
    {
        await using var h = await PublicHarness.StartAsync(accessToken: Token, listenerOf: _ => Listener.Tailnet);
        // The Tailscale handler decides there (403 here: a TestServer caller is not on the Unix socket).
        Assert.Contains(await OAuthFlowTests.InitializeAsync(h, Token), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task The_token_does_not_open_an_unconfigured_connector()
    {
        await using var h = await PublicHarness.StartAsync(configured: false, accessToken: Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await OAuthFlowTests.InitializeAsync(h, Token));
    }

    [Fact]
    public async Task The_summary_says_the_token_is_on_without_showing_it()
    {
        await using var h = await PublicHarness.StartAsync(accessToken: Token);
        var line = Assert.Single(h.Logs.Lines, l => l.StartsWith("unifi-mcp on ", StringComparison.Ordinal));
        Assert.Contains("access token on", line);
        Assert.DoesNotContain(Token, string.Join("\n", h.Logs.Lines));
    }
}
