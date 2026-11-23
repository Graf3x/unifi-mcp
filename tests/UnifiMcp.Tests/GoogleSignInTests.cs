using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace UnifiMcp.Tests;

public class GoogleSignInTests
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    [Fact]
    public async Task Allowed_account_gets_the_cookie()
    {
        await using var h = await PublicHarness.StartAsync();
        var signIn = await h.SignInAsync(PublicHarness.GoogleEmail);

        Assert.Equal(HttpStatusCode.Redirect, signIn.Callback.StatusCode);
        Assert.Equal("/connections", signIn.Callback.Headers.Location!.OriginalString);
        var cookie = h.Cookies.Get(PublicHarness.BaseAddress, GoogleSignIn.CookieScheme);
        Assert.NotNull(cookie);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        var line = h.Cookies.LastSetCookie(GoogleSignIn.CookieScheme)!;
        Assert.Contains("samesite=lax", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", line, StringComparison.OrdinalIgnoreCase);
        // Persistent for the 30-day sliding window.
        Assert.InRange(cookie.Expires.ToUniversalTime(), h.TimeProvider.GetUtcNow().UtcDateTime.AddDays(30).AddMinutes(-1),
            h.TimeProvider.GetUtcNow().UtcDateTime.AddDays(30).AddMinutes(1));
        // Sign-in leaves no OIDC correlation or nonce cookie behind.
        Assert.Equal([GoogleSignIn.CookieScheme], h.Cookies.Container.GetCookies(PublicHarness.BaseAddress).Select(c => c.Name));

        var page = await h.Http.GetAsync("/connections");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Signed in", await page.Content.ReadAsStringAsync());

        Assert.Contains(h.Logs.Lines, l => l.Contains(PublicHarness.GoogleEmail) && l.Contains("Allowed"));
        var state = QueryHelpers.ParseQuery(signIn.GoogleRedirect.Query)["state"].ToString();
        string[] secrets = [.. h.Google.Issued, cookie.Value, h.Google.ClientSecret, state];
        foreach (var secret in secrets)
            Assert.DoesNotContain(h.Logs.Lines, l => l.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_account_gets_refused_page_and_no_cookie()
    {
        await using var h = await PublicHarness.StartAsync();
        var signIn = await h.SignInAsync("someone@else.test");

        Assert.Equal(HttpStatusCode.Forbidden, signIn.Callback.StatusCode);
        Assert.Equal("text/html", signIn.Callback.Content.Headers.ContentType?.MediaType);
        Assert.Contains("isn't allowed", await signIn.Callback.Content.ReadAsStringAsync());
        Assert.Null(h.Cookies.LastSetCookie(GoogleSignIn.CookieScheme));
        Assert.Empty(h.Cookies.Container.GetCookies(PublicHarness.BaseAddress)); // no half-signed-in state
        Assert.Contains(h.Logs.Lines, l => l.Contains("someone@else.test") && l.Contains("NotListed"));
        Assert.Equal(HttpStatusCode.Redirect, (await h.Http.GetAsync("/connections")).StatusCode);
    }

    [Fact]
    public async Task Unverified_email_is_refused()
    {
        await using var h = await PublicHarness.StartAsync();
        var signIn = await h.SignInAsync(PublicHarness.GoogleEmail, verified: false);

        Assert.Equal(HttpStatusCode.Forbidden, signIn.Callback.StatusCode);
        Assert.Null(h.Cookies.LastSetCookie(GoogleSignIn.CookieScheme));
        Assert.Empty(h.Cookies.Container.GetCookies(PublicHarness.BaseAddress));
        Assert.Contains(h.Logs.Lines, l => l.Contains(PublicHarness.GoogleEmail) && l.Contains("Unverified"));
    }

    [Fact]
    public async Task Account_without_email_is_refused()
    {
        await using var h = await PublicHarness.StartAsync();
        var signIn = await h.SignInAsync(" ");

        Assert.Equal(HttpStatusCode.Forbidden, signIn.Callback.StatusCode);
        Assert.Null(h.Cookies.LastSetCookie(GoogleSignIn.CookieScheme));
        Assert.Contains(h.Logs.Lines, l => l.Contains("(no email)") && l.Contains("NoEmail"));
    }

    [Fact]
    public async Task Email_verified_sent_as_a_string_is_accepted()
    {
        await using var h = await PublicHarness.StartAsync();
        var signIn = await h.SignInAsync(PublicHarness.GoogleEmail, verifiedAsString: true);
        Assert.Equal(HttpStatusCode.Redirect, signIn.Callback.StatusCode);
        Assert.NotNull(h.Cookies.Get(PublicHarness.BaseAddress, GoogleSignIn.CookieScheme));
    }

    [Fact]
    public async Task Callback_is_a_get_not_a_form_post()
    {
        await using var h = await PublicHarness.StartAsync();
        var signIn = await h.SignInAsync(PublicHarness.GoogleEmail);

        var q = QueryHelpers.ParseQuery(signIn.GoogleRedirect.Query);
        Assert.Equal("query", q["response_mode"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("openid email profile", q["scope"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("https://" + PublicHarness.Host + "/signin-google", q["redirect_uri"]);
        Assert.Equal(HttpStatusCode.Redirect, signIn.Callback.StatusCode); // the GET callback signed in

        // A cross-site form post to the callback is refused before the OIDC handler sees it.
        using var post = new HttpRequestMessage(HttpMethod.Post, "/signin-google")
        {
            Content = new StringContent("code=x&state=y", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        post.Headers.Add("Origin", "https://accounts.google.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.SendAsync(post)).StatusCode);
    }

    [Fact]
    public async Task Removed_address_loses_the_cookie_at_once()
    {
        await using var h = await PublicHarness.StartAsync();
        await h.SignInAsync(PublicHarness.GoogleEmail);
        Assert.Equal(HttpStatusCode.OK, (await h.Http.GetAsync("/connections")).StatusCode);

        File.WriteAllText(h.AllowedGoogleEmailsFile, "");
        var after = await h.Http.GetAsync("/connections");

        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
        Assert.Null(h.Cookies.Get(PublicHarness.BaseAddress, GoogleSignIn.CookieScheme));
        Assert.Contains("expires=Thu, 01 Jan 1970", h.Cookies.LastSetCookie(GoogleSignIn.CookieScheme)!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(h.Logs.Lines, l => l.Contains(PublicHarness.GoogleEmail) && l.Contains("NotListed"));
    }

    [Fact]
    public async Task Cookie_older_than_90_days_is_rejected()
    {
        await using var h = await PublicHarness.StartAsync();
        // The cookie handler's own expiry follows the host's clock, so the 30-day window is tested by the same clock.
        Assert.Same(h.TimeProvider, h.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(GoogleSignIn.CookieScheme).TimeProvider);
        await h.SignInAsync(PublicHarness.GoogleEmail);

        for (var day = 1; day <= 90; day++)
        {
            h.TimeProvider.Advance(TimeSpan.FromDays(1));
            Assert.True((await h.Http.GetAsync("/connections")).StatusCode == HttpStatusCode.OK, $"day {day}");
        }
        h.TimeProvider.Advance(TimeSpan.FromDays(1));
        var day91 = await h.Http.GetAsync("/connections");

        Assert.NotEqual(HttpStatusCode.OK, day91.StatusCode);
        Assert.Null(h.Cookies.Get(PublicHarness.BaseAddress, GoogleSignIn.CookieScheme));
    }

    [Fact]
    public async Task Cookie_left_unused_for_31_days_expires()
    {
        await using var h = await PublicHarness.StartAsync();
        await h.SignInAsync(PublicHarness.GoogleEmail);
        h.TimeProvider.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.Redirect, (await h.Http.GetAsync("/connections")).StatusCode);
    }

    [Fact]
    public async Task Cookie_never_authenticates_mcp()
    {
        await using var h = await PublicHarness.StartAsync();
        await h.SignInAsync(PublicHarness.GoogleEmail);
        Assert.NotNull(h.Cookies.Get(PublicHarness.BaseAddress, GoogleSignIn.CookieScheme));

        var response = await h.Http.PostAsync("/mcp", new StringContent(Initialize, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/connections", true)]
    [InlineData("GET", "/connections/abc", true)]
    [InlineData("HEAD", "/oauth/authorize", true)]
    [InlineData("GET", "/oauth/authorize", true)]
    [InlineData("GET", "/oauth/authorize/", true)]
    [InlineData("GET", "/connectionsX", false)]
    [InlineData("POST", "/connections", false)]
    [InlineData("POST", "/oauth/authorize", false)]
    [InlineData("GET", "/oauth/token", false)]
    [InlineData("GET", "/mcp", false)]
    public void Only_page_navigations_are_sent_to_google(string method, string path, bool redirects) =>
        Assert.Equal(redirects, GoogleSignIn.SendsToGoogle(method, new PathString(path)));

    [Fact]
    public void Error_page_encodes_its_inputs()
    {
        var html = Pages.ErrorPage("<b>title</b>", "x & <script>alert(1)</script> \"q\"");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("x &amp; ", html);
        Assert.Contains("&quot;q&quot;", html);
    }
}
