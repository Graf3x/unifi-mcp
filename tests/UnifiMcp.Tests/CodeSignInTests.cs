using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace UnifiMcp.Tests;

/// <summary>MCP_SIGNIN=code: the owner signs in with a one-time code from the server log instead of Google.</summary>
public sealed partial class CodeSignInTests
{
    private static Task<PublicHarness> StartAsync(string? dataDir = null) => PublicHarness.StartAsync(signIn: SignInMode.Code, dataDir: dataDir);

    [Fact]
    public async Task Code_mode_is_configured_without_any_google_value()
    {
        await using var h = await StartAsync();
        Assert.Contains("connector ready", Assert.Single(h.Logs.Lines, l => l.StartsWith("unifi-mcp on ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_owner_page_sends_the_browser_to_the_code_page_not_google()
    {
        await using var h = await StartAsync();
        var r = await h.Http.GetAsync("/connections");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(CodeSignIn.Path + "?returnUrl=%2Fconnections", r.Headers.Location!.OriginalString);
        Assert.DoesNotContain(FakeGoogle.Authority, r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_code_page_logs_a_code_and_the_right_code_signs_in()
    {
        await using var h = await StartAsync();
        var page = await h.Http.GetAsync(CodeSignIn.Path + "?returnUrl=%2Fconnections");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("name=\"code\"", html);
        var code = h.LatestSignInCode();
        Assert.DoesNotContain(code, html); // only the log has it

        var post = await h.PostCodeAsync(code);
        Assert.Equal(HttpStatusCode.SeeOther, post.StatusCode);
        Assert.Equal("/connections", post.Headers.Location!.OriginalString);
        var connections = await h.Http.GetAsync("/connections");
        Assert.Equal(HttpStatusCode.OK, connections.StatusCode);
        Assert.Contains("signed in with a code", await connections.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_same_code_is_shown_again_until_it_is_used()
    {
        await using var h = await StartAsync();
        await h.Http.GetAsync(CodeSignIn.Path);
        var first = h.LatestSignInCode();
        await h.Http.GetAsync(CodeSignIn.Path);
        Assert.Equal(first, h.LatestSignInCode());
    }

    [Fact]
    public async Task A_wrong_code_is_refused_without_a_cookie()
    {
        await using var h = await StartAsync();
        await h.Http.GetAsync(CodeSignIn.Path);
        var post = await h.PostCodeAsync(Wrong(h.LatestSignInCode()));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Null(h.Cookies.Get(PublicHarness.BaseAddress, GoogleSignIn.CookieScheme));
        Assert.Equal(HttpStatusCode.Redirect, (await h.Http.GetAsync("/connections")).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_codes_burn_the_code_and_the_next_page_issues_a_new_one()
    {
        await using var h = await StartAsync();
        await h.Http.GetAsync(CodeSignIn.Path);
        var code = h.LatestSignInCode();
        for (var i = 0; i < SignInCodes.MaxAttempts; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await h.PostCodeAsync(Wrong(code))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostCodeAsync(code)).StatusCode);

        await h.Http.GetAsync(CodeSignIn.Path);
        Assert.NotEqual(code, h.LatestSignInCode());
    }

    [Fact]
    public async Task A_code_works_once()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.SeeOther, (await h.CodeSignInAsync()).StatusCode);
        var used = h.LatestSignInCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostCodeAsync(used)).StatusCode);
    }

    [Fact]
    public async Task A_code_expires_after_ten_minutes()
    {
        await using var h = await StartAsync();
        await h.Http.GetAsync(CodeSignIn.Path);
        var code = h.LatestSignInCode();
        h.TimeProvider.Advance(SignInCodes.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostCodeAsync(code)).StatusCode);
    }

    [Fact]
    public async Task Spaces_and_a_missing_dash_are_forgiven()
    {
        await using var h = await StartAsync();
        var r = await h.CodeSignInAsync(alter: c => " " + c.Replace("-", " ") + " ");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
    }

    [Fact]
    public async Task New_codes_are_capped_per_hour()
    {
        await using var h = await StartAsync();
        for (var i = 0; i < SignInCodes.MaxCodesPerHour; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await h.Http.GetAsync(CodeSignIn.Path)).StatusCode);
            var code = h.LatestSignInCode();
            for (var j = 0; j < SignInCodes.MaxAttempts; j++) await h.PostCodeAsync(Wrong(code), ip: $"10.0.{i}.{j}");
        }
        var refused = await h.Http.GetAsync(CodeSignIn.Path);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("Try again later", await refused.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Only_a_local_return_url_is_followed(string returnUrl)
    {
        await using var h = await StartAsync();
        await h.Http.GetAsync(CodeSignIn.Path);
        var post = await h.PostCodeAsync(h.LatestSignInCode(), returnUrl);
        Assert.Equal(HttpStatusCode.SeeOther, post.StatusCode);
        Assert.Equal("/connections", post.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task There_is_no_google_callback_in_code_mode()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync("/signin-google?code=x&state=y")).StatusCode);
    }

    [Fact]
    public async Task Google_mode_has_no_code_page()
    {
        await using var h = await PublicHarness.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync(CodeSignIn.Path)).StatusCode);
    }

    [Fact]
    public async Task The_code_page_is_not_on_the_tailnet()
    {
        await using var h = await PublicHarness.StartAsync(signIn: SignInMode.Code, listenerOf: _ => Listener.Tailnet);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync(CodeSignIn.Path)).StatusCode);
    }

    [Fact]
    public async Task The_full_connector_flow_works_with_a_code()
    {
        await using var h = await StartAsync();
        var (clientId, tokens) = await ConnectAsync(h);
        Assert.Equal(HttpStatusCode.OK, await OAuthFlowTests.InitializeAsync(h, tokens.Access));
        var (status, _) = await OAuthFlowTests.RefreshAsync(h, clientId, tokens.Refresh);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Switching_to_google_ends_what_a_code_sign_in_connected()
    {
        // One data folder (store and keyring) outlives both hosts, as ./data does across a restart with a new MCP_SIGNIN.
        var dataDir = Directory.CreateTempSubdirectory("unifi-mcp-switch").FullName;
        try
        {
            string clientId, refresh;
            await using (var code = await StartAsync(dataDir))
            {
                (clientId, var tokens) = await ConnectAsync(code);
                refresh = tokens.Refresh;
            }
            await using var google = await PublicHarness.StartAsync(dataDir: dataDir);
            await OAuthFlowTests.AssertInvalidGrantAsync(OAuthFlowTests.RefreshAsync(google, clientId, refresh));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dataDir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------------- helpers

    private static string Wrong(string code) => code == "00000-00000" ? "00000-00001" : "00000-00000";

    /// <summary>Register, authorize (signing in with a code), allow, exchange.</summary>
    private static async Task<(string ClientId, OAuthFlowTests.Tokens Tokens)> ConnectAsync(PublicHarness h)
    {
        var clientId = await OAuthFlowTests.RegisterAsync(h);
        var url = OAuthFlowTests.AuthorizeUrl(clientId);
        var signedIn = await h.CodeSignInAsync(start: url);
        Assert.Equal(HttpStatusCode.SeeOther, signedIn.StatusCode);
        Assert.Equal(url, signedIn.Headers.Location!.OriginalString);

        var page = await h.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("signed in with a code", html);
        var request = RequestField().Match(html).Groups[1].Value;
        using var consent = new HttpRequestMessage(HttpMethod.Post, "/oauth/consent")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["request"] = request, ["decision"] = "allow" }),
        };
        consent.Headers.Add("Origin", OAuthFlowTests.Base);
        var back = await h.Http.SendAsync(consent);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var code = QueryHelpers.ParseQuery(back.Headers.Location!.Query)["code"].ToString();
        return (clientId, await OAuthFlowTests.ExchangeAsync(h, clientId, code));
    }

    [GeneratedRegex("name=\"request\" value=\"([^\"]+)\"")]
    private static partial Regex RequestField();
}
