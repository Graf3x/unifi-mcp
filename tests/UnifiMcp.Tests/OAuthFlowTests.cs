using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

public sealed partial class OAuthFlowTests
{
    internal const string Base = "https://unifi.example.com";
    internal const string Resource = Base + "/mcp";
    internal const string Callback = "https://claude.ai/api/mcp/auth_callback";
    private const string CimdClientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"; // RFC 7636 appendix B
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    // ---------------------------------------------------------------------------------------------- helpers

    internal sealed record Tokens(string Access, string Refresh, JsonElement Body);

    internal static async Task<string> RegisterAsync(PublicHarness h, string? ip = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/register")
        {
            Content = JsonContent.Create(new
            {
                client_name = "Claude",
                redirect_uris = new[] { Callback },
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
            }),
        };
        if (ip is not null) request.Headers.Add("CF-Connecting-IP", ip);
        using var client = h.NewClient();
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    internal static string AuthorizeUrl(string clientId, string challenge = Challenge, string? resource = Resource, string state = "st-1") =>
        QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Callback,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["scope"] = "mcp:tools",
            ["state"] = state,
            ["resource"] = resource,
        }.Where(kv => kv.Value is not null));

    /// <summary>The query of a redirect back to Claude's callback.</summary>
    private static Dictionary<string, string> CallbackQuery(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(Callback + "?", location.ToString());
        return QueryHelpers.ParseQuery(location.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }

    /// <summary>The browser's part: authorize, sign in with Google if needed, allow on the consent page. Returns the code.</summary>
    /// <param name="expectConsentPage">Fail if a standing consent skips the page.</param>
    internal static async Task<string> AuthorizeAsync(PublicHarness h, string clientId, string challenge = Challenge, bool expectConsentPage = false)
    {
        var url = AuthorizeUrl(clientId, challenge);
        var page = await h.Http.GetAsync(url);
        if (page.StatusCode == HttpStatusCode.Redirect && page.Headers.Location!.ToString().StartsWith(FakeGoogle.AuthorizeEndpoint, StringComparison.Ordinal))
        {
            var signIn = await h.SignInAsync(PublicHarness.GoogleEmail, start: url);
            Assert.Equal(HttpStatusCode.Redirect, signIn.Callback.StatusCode);
            Assert.Equal(url, signIn.Callback.Headers.Location!.OriginalString);
            page = await h.Http.GetAsync(url);
        }
        if (page.StatusCode == HttpStatusCode.Redirect && !expectConsentPage) return CallbackQuery(page)["code"]; // standing consent

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("UniFi Network", html);
        var pending = RequestField().Match(html);
        Assert.True(pending.Success, html);

        using var consent = new HttpRequestMessage(HttpMethod.Post, "/oauth/consent")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["request"] = pending.Groups[1].Value, ["decision"] = "allow" }),
        };
        consent.Headers.Add("Origin", Base);
        var q = CallbackQuery(await h.Http.SendAsync(consent));
        Assert.Equal("st-1", q["state"]);
        return q["code"];
    }

    [GeneratedRegex("name=\"request\" value=\"([^\"]+)\"")]
    private static partial Regex RequestField();

    private static async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(PublicHarness h, Dictionary<string, string> form, string? ip = null)
    {
        using var client = h.NewClient();
        if (ip is not null) client.DefaultRequestHeaders.Add("CF-Connecting-IP", ip);
        var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    internal static async Task<Tokens> ExchangeAsync(PublicHarness h, string clientId, string code, string verifier = Verifier)
    {
        var (status, body) = await TokenAsync(h, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = Callback, ["resource"] = Resource,
        });
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(3600, body.GetProperty("expires_in").GetInt32());
        Assert.Equal("mcp:tools", body.GetProperty("scope").GetString());
        return new(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!, body);
    }

    internal static Task<(HttpStatusCode Status, JsonElement Body)> RefreshAsync(PublicHarness h, string clientId, string refresh, string? ip = null) =>
        TokenAsync(h, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refresh }, ip);

    private static async Task RevokeAsync(PublicHarness h, string refresh)
    {
        using var client = h.NewClient();
        var response = await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Tokens> RefreshOkAsync(PublicHarness h, string clientId, string refresh)
    {
        var (status, body) = await RefreshAsync(h, clientId, refresh);
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        return new(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!, body);
    }

    internal static async Task AssertInvalidGrantAsync(Task<(HttpStatusCode Status, JsonElement Body)> call)
    {
        var (status, body) = await call;
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
    }

    internal static async Task<HttpStatusCode> InitializeAsync(PublicHarness h, string? accessToken)
    {
        using var client = h.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await client.SendAsync(request)).StatusCode;
    }

    private static async Task<McpClient> McpClientAsync(PublicHarness h, string accessToken)
    {
        var http = h.NewClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Resource),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "claude-as-tested",
        }, http, loggerFactory: null, ownsHttpClient: true));
    }

    /// <summary>
    /// Serves Claude's client metadata document in place of the internet, with the fields Claude's real one at
    /// <see cref="CimdClientId"/> carries (the jwt-bearer grant included, which this server never offers).
    /// </summary>
    private sealed class FakeMetadata : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri!.ToString() != CimdClientId) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var doc = JsonSerializer.Serialize(new
            {
                client_id = CimdClientId,
                client_name = "Claude",
                client_uri = "https://claude.ai",
                redirect_uris = new[] { Callback },
                grant_types = new[] { "authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:jwt-bearer" },
                response_types = new[] { "code" },
                token_endpoint_auth_method = "none",
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(doc, Encoding.UTF8, "application/json") });
        }
    }

    private static Action<WebApplicationBuilder> ServeMetadata(FakeMetadata fake) => b =>
        b.Services.PostConfigure<HttpClientFactoryOptions>(HttpClientMetadataDocuments.HttpClientName,
            o => o.HttpMessageHandlerBuilderActions.Add(x => x.PrimaryHandler = fake));

    private static string Mint(SecurityKey key, DateTime now, string issuer = Base, string audience = Resource) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddHours(1),
            TokenType = "at+jwt",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "google-sub-owner", ["email"] = PublicHarness.GoogleEmail, ["name"] = "Test Owner",
                ["client_id"] = "mcp_test", ["scope"] = "mcp:tools", ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

    // ---------------------------------------------------------------------------------------------- discovery and bearer

    [Fact]
    public async Task No_token_gets_401_with_resource_metadata()
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        // cloudflared reaches the socket over plain http; the advertised URL is still the public https one.
        foreach (var url in new[] { "/mcp", "http://" + PublicHarness.Host + "/mcp" })
        {
            var response = await client.PostAsync(url, new StringContent(Initialize, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var challenge = response.Headers.WwwAuthenticate.ToString();
            Assert.Contains("Bearer", challenge);
            Assert.Contains("resource_metadata=\"https://unifi.example.com/.well-known/oauth-protected-resource", challenge);
        }

        var metadataUrl = Regex.Match((await client.PostAsync("/mcp", new StringContent(Initialize, Encoding.UTF8, "application/json")))
            .Headers.WwwAuthenticate.ToString(), "resource_metadata=\"([^\"]+)\"").Groups[1].Value;
        var prm = await client.GetFromJsonAsync<JsonElement>(metadataUrl);
        Assert.Equal(Resource, prm.GetProperty("resource").GetString());
        Assert.Equal([Base], prm.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["mcp:tools"], prm.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("UniFi Network", prm.GetProperty("resource_name").GetString());
    }

    [Fact]
    public async Task Metadata_pins_the_supported_flows()
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        foreach (var path in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" })
        {
            var m = await client.GetFromJsonAsync<JsonElement>(path);
            string[] Strings(string name) => [.. m.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];
            Assert.Equal(Base, m.GetProperty("issuer").GetString());
            Assert.Equal(Base + "/oauth/authorize", m.GetProperty("authorization_endpoint").GetString());
            Assert.Equal(Base + "/oauth/token", m.GetProperty("token_endpoint").GetString());
            Assert.Equal(Base + "/oauth/register", m.GetProperty("registration_endpoint").GetString());
            Assert.Equal(Base + "/oauth/revoke", m.GetProperty("revocation_endpoint").GetString());
            Assert.Equal(Base + "/oauth/jwks", m.GetProperty("jwks_uri").GetString());
            Assert.Equal(["S256"], Strings("code_challenge_methods_supported"));
            Assert.Equal(["authorization_code", "refresh_token"], Strings("grant_types_supported"));
            Assert.Equal(["none"], Strings("token_endpoint_auth_methods_supported"));
            Assert.Equal(["code"], Strings("response_types_supported"));
            Assert.Equal(["mcp:tools"], Strings("scopes_supported"));
            Assert.True(m.GetProperty("client_id_metadata_document_supported").GetBoolean());
        }

        var jwks = await client.GetFromJsonAsync<JsonElement>("/oauth/jwks");
        var key = Assert.Single(jwks.GetProperty("keys").EnumerateArray());
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.False(key.TryGetProperty("d", out _)); // public half only
    }

    [Fact]
    public async Task Tailnet_has_no_authorization_server_metadata()
    {
        await using var h = await PublicHarness.StartAsync(
            listenerOf: ctx => ctx.Request.Headers.ContainsKey("X-Test-Tailnet") ? Listener.Tailnet : Listener.Public);
        using var tailnet = h.NewClient();
        tailnet.DefaultRequestHeaders.Add("X-Test-Tailnet", "1");
        tailnet.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Harness.Login);
        using var internet = h.NewClient();
        foreach (var path in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration",
                     "/.well-known/oauth-authorization-server/mcp", "/.well-known/openid-configuration/mcp", "/oauth/jwks", "/oauth/register" })
            Assert.Equal(HttpStatusCode.NotFound, (await tailnet.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await internet.GetAsync("/.well-known/oauth-authorization-server")).StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("other-key")]
    [InlineData("expired")]
    public async Task Bad_tokens_get_401(string kind)
    {
        await using var h = await PublicHarness.StartAsync();
        var issuer = h.App.Services.GetRequiredService<ITokenIssuer>();
        var key = await issuer.GetSigningKeyAsync();
        var now = h.TimeProvider.GetUtcNow().UtcDateTime;
        // The same construction with nothing wrong is accepted, so each case fails for its one reason.
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(h, Mint(key, now)));

        string token;
        switch (kind)
        {
            case "wrong-audience": token = Mint(key, now, audience: Base + "/other"); break;
            case "wrong-issuer": token = Mint(key, now, issuer: "https://evil.example"); break;
            case "other-key":
                using (var other = RSA.Create(2048)) token = Mint(new RsaSecurityKey(other.ExportParameters(true)) { KeyId = key.KeyId }, now);
                break;
            default:
                token = await issuer.IssueAccessTokenAsync(new OAuthUser { Subject = "s", Email = PublicHarness.GoogleEmail, Name = "n" }, "mcp_x", "mcp:tools");
                Assert.Equal(HttpStatusCode.OK, await InitializeAsync(h, token));
                // One hour plus the two-minute clock skew, plus a minute: the brief's "+61 min" is still inside the skew.
                h.TimeProvider.Advance(TimeSpan.FromMinutes(63));
                break;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(h, token));
    }

    // ---------------------------------------------------------------------------------------------- the flow

    [Fact]
    public async Task Full_flow_with_registered_client()
    {
        await using var h = await PublicHarness.StartAsync(writes: true);
        h.Unifi.WithSite();
        var clientId = await RegisterAsync(h);
        Assert.StartsWith("mcp_", clientId);

        var code = await AuthorizeAsync(h, clientId);
        var tokens = await ExchangeAsync(h, clientId, code);

        await using (var mcp = await McpClientAsync(h, tokens.Access))
        {
            Assert.NotNull(mcp.ServerInfo);
            Assert.Equal("UniFi Network", mcp.ServerInfo.Title);
            Assert.Equal(Base, mcp.ServerInfo.WebsiteUrl);
            Assert.Equal([Base + "/mcp/icon-512.png", Base + "/mcp/icon-180.png", Base + "/mcp/icon-32.png"],
                mcp.ServerInfo.Icons!.Select(i => i.Source));
            var tools = await mcp.ListToolsAsync();
            Assert.Contains(tools, t => t.Name == "create_acl_rule" && t.ProtocolTool.Annotations?.DestructiveHint == true);
            var result = await mcp.CallToolAsync("getting_started");
            Assert.NotEqual(true, result.IsError);
        }

        var refreshed = await RefreshOkAsync(h, clientId, tokens.Refresh);
        Assert.NotEqual(tokens.Refresh, refreshed.Refresh);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(h, refreshed.Access));

        using (var client = h.NewClient())
        {
            var revoke = await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["token"] = refreshed.Refresh, ["client_id"] = clientId }));
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, refreshed.Refresh));

        Assert.Contains(h.Logs.Lines, l => l.Contains(clientId) && l.Contains("registered"));
        Assert.Contains(h.Logs.Lines, l => l.Contains(clientId) && l.Contains(PublicHarness.GoogleEmail) && l.Contains("issued"));
        Assert.Contains(h.Logs.Lines, l => l.Contains(clientId) && l.Contains("revoked"));
    }

    [Fact]
    public async Task Full_flow_with_cimd_client()
    {
        var metadata = new FakeMetadata();
        await using var h = await PublicHarness.StartAsync(writes: true, configure: ServeMetadata(metadata));

        var code = await AuthorizeAsync(h, CimdClientId);
        var tokens = await ExchangeAsync(h, CimdClientId, code);
        Assert.True(metadata.Calls >= 1);

        await using (var mcp = await McpClientAsync(h, tokens.Access))
            Assert.Contains(await mcp.ListToolsAsync(), t => t.Name == "create_acl_rule");

        var refreshed = await RefreshOkAsync(h, CimdClientId, tokens.Refresh);
        using (var client = h.NewClient())
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/oauth/revoke",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshed.Refresh }))).StatusCode);
        await AssertInvalidGrantAsync(RefreshAsync(h, CimdClientId, refreshed.Refresh));
    }

    [Fact]
    public async Task Consent_page_names_the_metadata_host_and_allows_its_redirect()
    {
        var metadata = new FakeMetadata();
        await using var h = await PublicHarness.StartAsync(configure: ServeMetadata(metadata));
        var url = AuthorizeUrl(CimdClientId);
        await h.SignInAsync(PublicHarness.GoogleEmail, start: url);

        var page = await h.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("claude.ai", html);
        Assert.Contains(PublicHarness.GoogleEmail, html);
        // The browser follows the consent POST's redirect to Claude, so the page's form-action must allow that origin.
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action 'self' https://claude.ai", csp);
    }

    [Fact]
    public async Task Second_authorize_skips_consent()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);
        await ExchangeAsync(h, clientId, await AuthorizeAsync(h, clientId));

        var again = await h.Http.GetAsync(AuthorizeUrl(clientId));
        var q = CallbackQuery(again);
        Assert.Equal("st-1", q["state"]);
        await ExchangeAsync(h, clientId, q["code"]);
    }

    [Fact]
    public async Task Reused_refresh_token_revokes_the_family()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);
        var first = await ExchangeAsync(h, clientId, await AuthorizeAsync(h, clientId));
        var second = await RefreshOkAsync(h, clientId, first.Refresh);

        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, first.Refresh));
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, second.Refresh));
        Assert.Contains(h.Logs.Lines, l => l.Contains("reuse") && l.Contains(clientId));
    }

    [Fact]
    public async Task Removed_address_fails_the_next_refresh()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);
        var tokens = await ExchangeAsync(h, clientId, await AuthorizeAsync(h, clientId));

        File.WriteAllText(h.AllowedGoogleEmailsFile, "");
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, tokens.Refresh));

        // The family is gone, not just paused: putting the address back does not revive the token.
        File.WriteAllText(h.AllowedGoogleEmailsFile, PublicHarness.GoogleEmail + "\n");
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, tokens.Refresh));
        Assert.Contains(h.Logs.Lines, l => l.Contains(PublicHarness.GoogleEmail) && l.Contains("NotAllowed"));
    }

    [Fact]
    public async Task Removed_address_closes_authorize()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);
        await ExchangeAsync(h, clientId, await AuthorizeAsync(h, clientId));

        File.WriteAllText(h.AllowedGoogleEmailsFile, "");
        var again = await h.Http.GetAsync(AuthorizeUrl(clientId));
        // The cookie no longer opens authorize: the browser is sent back to Google, and no code is issued.
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.StartsWith(FakeGoogle.AuthorizeEndpoint, again.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Refresh_after_restart_succeeds()
    {
        var dataDir = Directory.CreateTempSubdirectory("unifi-mcp-restart").FullName;
        try
        {
            string clientId;
            Tokens tokens;
            await using (var first = await PublicHarness.StartAsync(dataDir: dataDir))
            {
                clientId = await RegisterAsync(first);
                tokens = await ExchangeAsync(first, clientId, await AuthorizeAsync(first, clientId));
            }

            await using var second = await PublicHarness.StartAsync(dataDir: dataDir);
            // The old access token still validates: same signing key, unwrapped by the same keyring.
            Assert.Equal(HttpStatusCode.OK, await InitializeAsync(second, tokens.Access));
            var refreshed = await RefreshOkAsync(second, clientId, tokens.Refresh);
            Assert.Equal(HttpStatusCode.OK, await InitializeAsync(second, refreshed.Access));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Wrong_resource_is_invalid_target()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);

        var q = CallbackQuery(await h.Http.GetAsync(AuthorizeUrl(clientId, resource: "https://evil.example/mcp")));
        Assert.Equal("invalid_target", q["error"]);
        Assert.Equal("st-1", q["state"]);
        Assert.False(q.ContainsKey("code"));

        var code = await AuthorizeAsync(h, clientId);
        var (status, body) = await TokenAsync(h, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code,
            ["code_verifier"] = Verifier, ["resource"] = "https://evil.example/mcp",
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_target", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_client_or_redirect_gets_a_page_not_a_redirect()
    {
        await using var h = await PublicHarness.StartAsync();
        var unknown = await h.Http.GetAsync(AuthorizeUrl("mcp_nobody"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("text/html", unknown.Content.Headers.ContentType?.MediaType);
        Assert.Contains("UniFi Network", await unknown.Content.ReadAsStringAsync());

        var clientId = await RegisterAsync(h);
        var url = AuthorizeUrl(clientId).Replace(Uri.EscapeDataString(Callback), Uri.EscapeDataString("https://evil.example/cb"));
        var elsewhere = await h.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Null(elsewhere.Headers.Location);
    }

    [Fact]
    public async Task Register_is_rate_limited_per_client_ip()
    {
        await using var h = await PublicHarness.StartAsync();
        for (var i = 0; i < 10; i++) await RegisterAsync(h, ip: "203.0.113.7");

        using var client = h.NewClient();
        using var eleventh = new HttpRequestMessage(HttpMethod.Post, "/oauth/register")
        {
            Content = JsonContent.Create(new { redirect_uris = new[] { Callback } }),
        };
        eleventh.Headers.Add("CF-Connecting-IP", "203.0.113.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(eleventh)).StatusCode);

        await RegisterAsync(h, ip: "203.0.113.8");
    }

    [Fact]
    public async Task Register_refuses_http_and_too_many_redirects()
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        foreach (var uris in new[] { new[] { "http://claude.ai/cb" }, Enumerable.Range(0, 6).Select(i => $"https://claude.ai/cb{i}").ToArray(), Array.Empty<string>() })
        {
            var response = await client.PostAsJsonAsync("/oauth/register", new { redirect_uris = uris });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>Counts the request body bytes the app reads, ahead of all of its middleware.</summary>
    private sealed class BodyReadCounter : IStartupFilter
    {
        public long Read;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                ctx.Request.Body = new CountingStream(ctx.Request.Body, this);
                await n(ctx);
            });
            next(app);
        };

        private sealed class CountingStream(Stream inner, BodyReadCounter counter) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            private int Count(int n)
            {
                Interlocked.Add(ref counter.Read, n);
                return n;
            }
        }
    }

    /// <summary>A JSON client_name that never ends, sent chunked (no Content-Length): <paramref name="total"/> bytes in all.</summary>
    private sealed class EndlessJson(long total) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var head = Encoding.UTF8.GetBytes("{\"redirect_uris\":[\"" + Callback + "\"],\"client_name\":\"");
            await stream.WriteAsync(head);
            var chunk = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
            for (long written = head.Length; written < total; written += chunk.Length)
                await stream.WriteAsync(chunk);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    [Fact]
    public async Task Register_refuses_a_body_over_16_kb()
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        var body = JsonSerializer.Serialize(new { redirect_uris = new[] { Callback }, software_id = new string('a', 16 * 1024) });
        var response = await client.PostAsync("/oauth/register", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("invalid_client_metadata", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // Just under the cap still registers.
        var fits = JsonSerializer.Serialize(new { redirect_uris = new[] { Callback }, software_id = new string('a', 15 * 1024) });
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/oauth/register", new StringContent(fits, Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Register_stops_reading_a_chunked_body_at_the_cap()
    {
        var counter = new BodyReadCounter();
        await using var h = await PublicHarness.StartAsync(configure: b => b.Services.AddSingleton<IStartupFilter>(counter));
        using var client = h.NewClient();
        using var content = new EndlessJson(30L * 1024 * 1024);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/oauth/register", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(Interlocked.Read(ref counter.Read) < 256 * 1024, $"read {Interlocked.Read(ref counter.Read)} bytes");
    }

    [Fact]
    public async Task Register_caps_redirect_uri_and_client_name_lengths()
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        async Task<(HttpStatusCode, string?)> Register(object request)
        {
            var response = await client.PostAsJsonAsync("/oauth/register", request);
            return (response.StatusCode, response.StatusCode == HttpStatusCode.Created ? null
                : (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        var longUri = "https://claude.ai/" + new string('a', 2000 - "https://claude.ai/".Length);
        Assert.Equal((HttpStatusCode.Created, null), await Register(new { redirect_uris = new[] { longUri } }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_redirect_uri"), await Register(new { redirect_uris = new[] { longUri + "a" } }));

        Assert.Equal((HttpStatusCode.Created, null), await Register(new { client_name = new string('n', 200), redirect_uris = new[] { Callback } }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_client_metadata"),
            await Register(new { client_name = new string('n', 201), redirect_uris = new[] { Callback } }));
    }

    [Fact]
    public async Task Client_supplied_values_are_logged_on_one_line()
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        var registered = await client.PostAsJsonAsync("/oauth/register",
            new { client_name = "Evil\r\nFAKE entry\u2028\u0085\u001b[31m", redirect_uris = new[] { Callback } });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await AssertInvalidClientAsync(TokenAsync(h, new() { ["grant_type"] = "refresh_token", ["client_id"] = "mcp_x\r\nFAKE refusal\u2029", ["refresh_token"] = "t" }));

        static bool Unsafe(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';
        var registration = Assert.Single(h.Logs.Lines, l => l.Contains("registered"));
        Assert.Contains("EvilFAKE entry[31m", registration);
        Assert.DoesNotContain(registration, Unsafe);
        var refusal = Assert.Single(h.Logs.Lines, l => l.Contains("token request refused"));
        Assert.Contains("mcp_xFAKE refusal", refusal);
        Assert.DoesNotContain(refusal, Unsafe);
    }

    private static async Task AssertInvalidClientAsync(Task<(HttpStatusCode Status, JsonElement Body)> call)
    {
        var (status, body) = await call;
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("invalid_client", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Full_flow_logs_hold_no_secret_values()
    {
        await using var h = await PublicHarness.StartAsync(writes: true, configure: b => b.Logging.SetMinimumLevel(LogLevel.Trace));
        h.Unifi.WithSite();
        var clientId = await RegisterAsync(h);
        var code = await AuthorizeAsync(h, clientId);
        var tokens = await ExchangeAsync(h, clientId, code);
        await using (var mcp = await McpClientAsync(h, tokens.Access))
        {
            await mcp.ListToolsAsync();
            await mcp.CallToolAsync("call_read_operation", new Dictionary<string, object?> { ["operation"] = "get_info" });
        }
        var refreshed = await RefreshOkAsync(h, clientId, tokens.Refresh);
        using (var client = h.NewClient())
            await client.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshed.Refresh }));
        await RefreshAsync(h, clientId, tokens.Refresh); // reuse: logged as a warning

        Assert.Contains(h.Unifi.Requests, r => r.ApiKey == Harness.ApiKey); // the key was really used
        Assert.Contains(h.Logs.Lines, l => l.Contains("issued"));
        var cookie = h.Cookies.Get(PublicHarness.BaseAddress, GoogleSignIn.CookieScheme)!.Value;
        string[] secrets =
        [
            code, tokens.Access, tokens.Refresh, refreshed.Access, refreshed.Refresh, cookie,
            h.Google.ClientSecret, PublicHarness.DataKey, Harness.ApiKey, .. h.Google.Issued,
            // A token's signature alone is enough to spot one cut into a log line.
            tokens.Access.Split('.')[2],
        ];
        foreach (var (secret, i) in secrets.Select((s, i) => (s, i)))
        {
            var leak = h.Logs.Lines.FirstOrDefault(l => l.Contains(secret, StringComparison.Ordinal));
            Assert.True(leak is null, $"secret #{i} in the log: {leak}");
        }
    }

    // ---------------------------------------------------------------------------------------------- reconnecting after a revoke

    /// <summary>After the family is gone, connecting again must ask again and then give a refresh token that works.</summary>
    private static async Task ReconnectWorksAsync(PublicHarness h, string clientId)
    {
        var code = await AuthorizeAsync(h, clientId, expectConsentPage: true);
        var tokens = await ExchangeAsync(h, clientId, code);
        var refreshed = await RefreshOkAsync(h, clientId, tokens.Refresh);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(h, refreshed.Access));
        await RefreshOkAsync(h, clientId, refreshed.Refresh);
    }

    [Fact]
    public async Task Reconnect_after_revoke_asks_again_and_refresh_works()
    {
        var metadata = new FakeMetadata();
        await using var h = await PublicHarness.StartAsync(configure: ServeMetadata(metadata));
        // CIMD clients such as Claude keep one consent key for good, so they are the ones a stale family would trap.
        var tokens = await ExchangeAsync(h, CimdClientId, await AuthorizeAsync(h, CimdClientId));
        await RevokeAsync(h, tokens.Refresh);
        await ReconnectWorksAsync(h, CimdClientId);
    }

    [Fact]
    public async Task Reconnect_after_reuse_detection_asks_again_and_refresh_works()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);
        var first = await ExchangeAsync(h, clientId, await AuthorizeAsync(h, clientId));
        await RefreshOkAsync(h, clientId, first.Refresh);
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, first.Refresh)); // a benign retry looks the same
        await ReconnectWorksAsync(h, clientId);
    }

    [Fact]
    public async Task Reconnect_after_the_address_is_restored_asks_again_and_refresh_works()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);
        var tokens = await ExchangeAsync(h, clientId, await AuthorizeAsync(h, clientId));
        File.WriteAllText(h.AllowedGoogleEmailsFile, "");
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, tokens.Refresh));
        File.WriteAllText(h.AllowedGoogleEmailsFile, PublicHarness.GoogleEmail + "\n");
        await ReconnectWorksAsync(h, clientId);
    }

    [Fact]
    public async Task Refresh_with_wrong_resource_is_invalid_target()
    {
        await using var h = await PublicHarness.StartAsync();
        var clientId = await RegisterAsync(h);
        var tokens = await ExchangeAsync(h, clientId, await AuthorizeAsync(h, clientId));

        var (status, body) = await TokenAsync(h, new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = tokens.Refresh, ["resource"] = "https://evil.example/mcp",
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_target", body.GetProperty("error").GetString());

        // The token was not spent: the right resource (or none) still refreshes.
        var (ok, okBody) = await TokenAsync(h, new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = tokens.Refresh, ["resource"] = Resource,
        });
        Assert.True(ok == HttpStatusCode.OK, okBody.ToString());
    }

    [Fact]
    public async Task Token_refusals_are_logged_with_reason_and_ip_and_no_secrets()
    {
        const string ip = "203.0.113.9";
        await using var h = await PublicHarness.StartAsync(configure: b => b.Logging.SetMinimumLevel(LogLevel.Trace));
        var clientId = await RegisterAsync(h);
        var wrongVerifier = Verifier.Replace('d', 'e');

        var badPkceCode = await AuthorizeAsync(h, clientId);
        await AssertInvalidGrantAsync(TokenAsync(h, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = badPkceCode, ["code_verifier"] = wrongVerifier,
        }, ip));
        await AssertInvalidGrantAsync(TokenAsync(h, new() // the code was spent by the failed attempt
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = badPkceCode, ["code_verifier"] = Verifier,
        }, ip));

        var code = await AuthorizeAsync(h, clientId);
        var tokens = await ExchangeAsync(h, clientId, code);
        await TokenAsync(h, new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = tokens.Refresh, ["resource"] = "https://evil.example/mcp",
        }, ip);
        var refreshed = await RefreshOkAsync(h, clientId, tokens.Refresh);
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, tokens.Refresh, ip)); // reuse
        await AssertInvalidGrantAsync(RefreshAsync(h, clientId, "not-a-token-" + Guid.NewGuid(), ip)); // unknown

        string Line(string reason) => Assert.Single(h.Logs.Lines,
            l => l.Contains("refused") && l.Contains(reason, StringComparison.OrdinalIgnoreCase) && l.Contains(clientId) && l.Contains(ip));
        Line("PKCE");
        Line("code is invalid, expired or already used");
        Line("invalid_target");
        Line("reuse");
        Line("refresh token is invalid or expired");

        string[] secrets = [badPkceCode, code, Verifier, wrongVerifier, tokens.Access, tokens.Refresh, refreshed.Access, refreshed.Refresh];
        foreach (var (secret, i) in secrets.Select((s, i) => (s, i)))
        {
            var leak = h.Logs.Lines.FirstOrDefault(l => l.Contains(secret, StringComparison.Ordinal));
            Assert.True(leak is null, $"secret #{i} in the log: {leak}");
        }
    }

    // ---------------------------------------------------------------------------------------------- broken keyring

    [Fact]
    public async Task Lost_keyring_with_a_stored_signing_key_answers_503()
    {
        var dataDir = Directory.CreateTempSubdirectory("unifi-mcp-lostkeys").FullName;
        try
        {
            await using (var first = await PublicHarness.StartAsync(dataDir: dataDir))
            using (var client = first.NewClient())
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/oauth/jwks")).StatusCode); // generates and stores the key
            Directory.Delete(Path.Combine(dataDir, "keys"), recursive: true);

            // Same data key, empty keyring: the keyring check passes, but the stored signing key cannot be unwrapped.
            await using var second = await PublicHarness.StartAsync(dataDir: dataDir,
                listenerOf: ctx => ctx.Request.Headers.ContainsKey("X-Test-Tailnet") ? Listener.Tailnet : Listener.Public);
            using var internet = second.NewClient();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await internet.GetAsync("/oauth/jwks")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await internet.PostAsync("/mcp", new StringContent(Initialize, Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal("signing key cannot be unprotected with the current keyring", second.App.Services.GetRequiredService<ConnectorState>().Reason);

            using var tailnet = second.NewClient();
            tailnet.DefaultRequestHeaders.Add("X-Test-Tailnet", "1");
            Assert.Equal("ok", await tailnet.GetStringAsync("/healthz"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Unreadable_signing_key_found_while_serving_answers_503_not_500()
    {
        await using var h = await PublicHarness.StartAsync();
        // No key at startup; one appears that this keyring did not wrap.
        await new SqliteOAuthStore(Path.Combine(h.Settings.DataDir, "oauth.db"), System.TimeProvider.System)
            .SaveSigningKeyIfAbsentAsync(new SigningKey { KeyId = "k1", ProtectedPrivateKey = "CfDJ8not-a-real-payload" });

        using var client = h.NewClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/oauth/jwks")).StatusCode);
        Assert.Equal("signing key cannot be unprotected with the current keyring", h.App.Services.GetRequiredService<ConnectorState>().Reason);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/.well-known/oauth-authorization-server")).StatusCode);
    }
}
