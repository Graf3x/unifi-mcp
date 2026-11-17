using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace UnifiMcp.Tests;

public sealed class ConnectionsTests
{
    private const string Own = OAuthFlowTests.Base;
    /// <summary>The Google subject FakeGoogle gives the owner.</summary>
    private static readonly string Owner = "google-sub-" + PublicHarness.GoogleEmail.ToLowerInvariant();

    private static IOAuthStore Store(PublicHarness h) => h.App.Services.GetRequiredService<IOAuthStore>();

    /// <summary>Registers a client, connects it through the whole flow (the browser ends up signed in) and returns its tokens.</summary>
    private static async Task<(string ClientId, OAuthFlowTests.Tokens Tokens)> ConnectAsync(PublicHarness h)
    {
        var clientId = await OAuthFlowTests.RegisterAsync(h);
        var code = await OAuthFlowTests.AuthorizeAsync(h, clientId);
        return (clientId, await OAuthFlowTests.ExchangeAsync(h, clientId, code));
    }

    private static Task<HttpResponseMessage> DisconnectAsync(PublicHarness h, string clientId, string? origin = Own)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/connections/{Uri.EscapeDataString(clientId)}/disconnect")
        {
            Content = new FormUrlEncodedContent([]),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return h.Http.SendAsync(request);
    }

    private static async Task<string> PageAsync(PublicHarness h)
    {
        var page = await h.Http.GetAsync("/connections");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        return await page.Content.ReadAsStringAsync();
    }

    private static Consent NewConsent(string user, string clientId, string name, string? uri = null) => new()
    {
        UserId = user, ClientId = clientId, ClientName = name, ClientUri = uri, Scope = "mcp:tools", FamilyId = "fam-" + clientId,
        CreatedAt = DateTimeOffset.UtcNow, LastUsedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Connections_lists_the_connected_client()
    {
        await using var h = await PublicHarness.StartAsync();
        var (clientId, tokens) = await ConnectAsync(h);

        var response = await h.Http.GetAsync("/connections");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Claude", html);
        Assert.Contains($"action=\"/connections/{Uri.EscapeDataString(clientId)}/disconnect\"", html);
        Assert.Contains("Disconnect", html);
        Assert.Contains("Approved", html);
        Assert.Contains("Last used", html);
        Assert.Contains(h.TimeProvider.GetUtcNow().ToString("yyyy-MM-dd"), html);
        // No secret ever reaches the page.
        Assert.DoesNotContain(tokens.Access, html);
        Assert.DoesNotContain(tokens.Refresh, html);
    }

    [Fact]
    public async Task Connections_with_nothing_connected_says_so()
    {
        await using var h = await PublicHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await h.SignInAsync(PublicHarness.GoogleEmail)).Callback.StatusCode);
        Assert.Contains("No connected applications", await PageAsync(h));
    }

    [Fact]
    public async Task Disconnect_revokes_its_tokens()
    {
        await using var h = await PublicHarness.StartAsync();
        var (clientId, tokens) = await ConnectAsync(h);
        Assert.Equal(HttpStatusCode.OK, await OAuthFlowTests.InitializeAsync(h, tokens.Access));

        var response = await DisconnectAsync(h, clientId);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/connections", response.Headers.Location!.OriginalString);

        await OAuthFlowTests.AssertInvalidGrantAsync(OAuthFlowTests.RefreshAsync(h, clientId, tokens.Refresh));
        // The access token is a short-lived JWT: it stays valid until it expires (accepted).
        Assert.Equal(HttpStatusCode.OK, await OAuthFlowTests.InitializeAsync(h, tokens.Access));
        Assert.Null(await Store(h).GetConsentAsync(Owner, clientId));
        Assert.Contains("No connected applications", await PageAsync(h));

        var lines = h.Logs.Lines;
        var log = string.Join("\n", lines.Where(l => l.Contains("disconnected")));
        Assert.Contains(PublicHarness.GoogleEmail, log);
        Assert.Contains(clientId, log);
        Assert.DoesNotContain(tokens.Refresh, string.Join("\n", lines));
        Assert.DoesNotContain(tokens.Access, string.Join("\n", lines));
    }

    [Fact]
    public async Task Disconnect_needs_same_origin()
    {
        await using var h = await PublicHarness.StartAsync();
        var (clientId, tokens) = await ConnectAsync(h);

        Assert.Equal(HttpStatusCode.Forbidden, (await DisconnectAsync(h, clientId, "https://evil.example")).StatusCode);
        // Stricter than the request guard: no Origin at all is refused too.
        Assert.Equal(HttpStatusCode.Forbidden, (await DisconnectAsync(h, clientId, origin: null)).StatusCode);

        Assert.NotNull(await Store(h).GetConsentAsync(Owner, clientId));
        Assert.Contains(clientId, await PageAsync(h));
        Assert.Equal(HttpStatusCode.OK, (await OAuthFlowTests.RefreshAsync(h, clientId, tokens.Refresh)).Status);
    }

    [Fact]
    public async Task Disconnect_without_cookie_is_refused()
    {
        await using var h = await PublicHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await DisconnectAsync(h, "mcp_x")).StatusCode);
    }

    [Fact]
    public async Task Disconnect_of_an_unknown_or_foreign_client_is_404_and_changes_nothing()
    {
        await using var h = await PublicHarness.StartAsync();
        var (clientId, _) = await ConnectAsync(h);
        // Another user's consent: never listed, never deletable by the signed-in owner.
        await Store(h).UpsertConsentAsync(NewConsent("someone-else", "mcp_foreign", "Foreign"));

        Assert.Equal(HttpStatusCode.NotFound, (await DisconnectAsync(h, "mcp_foreign")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DisconnectAsync(h, "mcp_nobody")).StatusCode);

        Assert.NotNull(await Store(h).GetConsentAsync("someone-else", "mcp_foreign"));
        Assert.False(await Store(h).IsFamilyRevokedAsync("fam-mcp_foreign"));
        var html = await PageAsync(h);
        Assert.Contains(clientId, html);
        Assert.DoesNotContain("Foreign", html);
    }

    [Fact]
    public async Task A_client_id_with_reserved_characters_round_trips_through_the_form_action()
    {
        await using var h = await PublicHarness.StartAsync();
        await ConnectAsync(h); // signs the browser in as the owner
        const string url = "cimd_a b&c?d=1#e%f+g"; // route-hostile characters (a slash would be a path separator, which no key contains)
        await Store(h).UpsertConsentAsync(NewConsent(Owner, url, "claude.ai", url));

        var html = await PageAsync(h);
        Assert.Contains("action=\"/connections/cimd_a%20b%26c%3Fd%3D1%23e%25f%2Bg/disconnect\"", html);

        Assert.Equal(HttpStatusCode.SeeOther, (await DisconnectAsync(h, url)).StatusCode);
        Assert.Null(await Store(h).GetConsentAsync(Owner, url));
        Assert.True(await Store(h).IsFamilyRevokedAsync("fam-" + url));
    }

    [Fact]
    public async Task Client_text_is_encoded_and_truncated()
    {
        await using var h = await PublicHarness.StartAsync();
        await ConnectAsync(h);
        var longName = new string('n', 300);
        await Store(h).UpsertConsentAsync(NewConsent(Owner, "mcp_xss", "<script>alert(1)</script>\"><img src=x>", "https://<b>evil</b>.example/x"));
        await Store(h).UpsertConsentAsync(NewConsent(Owner, "mcp_long", longName));

        var html = await PageAsync(h);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>evil", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain(longName, html);
        Assert.Contains(new string('n', 100), html);
        Assert.DoesNotContain(new string('n', 101), html);
    }

    [Fact]
    public async Task Connections_without_cookie_goes_to_google()
    {
        await using var h = await PublicHarness.StartAsync();
        var response = await h.Http.GetAsync("/connections");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://google.test/", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData(512)]
    [InlineData(180)]
    [InlineData(32)]
    public async Task Icons_are_anonymous_pngs(int size)
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient(); // no cookie, no token
        var response = await client.GetAsync($"/mcp/icon-{size}.png");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=86400", response.Headers.CacheControl!.ToString());

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        // IHDR: width and height, big-endian, right after the chunk length and type.
        Assert.Equal("IHDR", Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.True(bytes.Length < 40_000, $"{bytes.Length} bytes");
    }

    [Fact]
    public async Task Icons_are_not_rate_limited()
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        for (var i = 0; i < 80; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/mcp/icon-32.png")).StatusCode);
    }

    [Fact]
    public async Task Icons_on_the_tailnet_are_served_too()
    {
        // Public, static artwork: the tailnet side needs no special case.
        await using var h = await PublicHarness.StartAsync(listenerOf: _ => Listener.Tailnet);
        Assert.Equal(HttpStatusCode.OK, (await h.Http.GetAsync("/mcp/icon-512.png")).StatusCode);
    }

    private static void AssertReferrerPolicy(string html, System.Net.Http.Headers.HttpResponseHeaders? headers = null)
    {
        // A no-referrer page makes the browser send "Origin: null" on form posts, which is (rightly) refused: see Pages.ReferrerPolicy.
        Assert.Contains("<meta name=\"referrer\" content=\"same-origin\">", html);
        Assert.DoesNotContain("no-referrer", html);
        if (headers is not null) Assert.Equal("same-origin", Assert.Single(headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task Every_page_uses_the_same_origin_referrer_policy()
    {
        AssertReferrerPolicy(Pages.Html("t", "x"));
        AssertReferrerPolicy(Pages.ErrorPage("t", "x"));

        await using var h = await PublicHarness.StartAsync();
        var clientId = await OAuthFlowTests.RegisterAsync(h);
        var authorize = OAuthFlowTests.AuthorizeUrl(clientId);
        await h.SignInAsync(PublicHarness.GoogleEmail, start: authorize);
        var consent = await h.Http.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        Assert.Contains("/oauth/consent", await consent.Content.ReadAsStringAsync());
        AssertReferrerPolicy(await consent.Content.ReadAsStringAsync(), consent.Headers);

        var connections = await h.Http.GetAsync("/connections");
        AssertReferrerPolicy(await connections.Content.ReadAsStringAsync(), connections.Headers);

        var error = await h.Http.GetAsync("/oauth/authorize?client_id=nobody");
        AssertReferrerPolicy(await error.Content.ReadAsStringAsync(), error.Headers);
    }

    [Fact]
    public async Task Origin_null_stays_refused()
    {
        await using var h = await PublicHarness.StartAsync();
        var (clientId, _) = await ConnectAsync(h);

        Assert.Equal(HttpStatusCode.Forbidden, (await DisconnectAsync(h, clientId, "null")).StatusCode);
        Assert.NotNull(await Store(h).GetConsentAsync(Owner, clientId));

        using var consent = new HttpRequestMessage(HttpMethod.Post, "/oauth/consent")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["request"] = "x", ["decision"] = "allow" }),
        };
        consent.Headers.Add("Origin", "null");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.SendAsync(consent)).StatusCode);
    }
}
