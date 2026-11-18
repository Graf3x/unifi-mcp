using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

/// <summary>The real host as the public (internet) listener sees it: every request arrives as Listener.Public.</summary>
public sealed class PublicHarness : IAsyncDisposable
{
    public const string Host = "unifi.example.com";
    public const string GoogleEmail = "owner@example.test";
    public const string DataKey = "0123456789abcdef0123456789abcdef";

    public static readonly Uri BaseAddress = new("https://" + Host);

    public FakeUnifi Unifi { get; } = new();
    public FakeGoogle Google { get; } = new();
    /// <summary>The host's clock (registered before the auth handlers, so cookie expiry follows it too).</summary>
    public FakeTimeProvider TimeProvider { get; } = new(DateTimeOffset.UtcNow);
    /// <summary>The browser's cookies. <see cref="Http"/> sends and stores them.</summary>
    public CookieJar Cookies { get; private set; } = null!;
    public WebApplication App { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public Settings Settings { get; private set; } = null!;
    public string Dir { get; } = Directory.CreateTempSubdirectory("unifi-mcp-public-test").FullName;
    /// <summary>The allow-list file the host reads on every check; tests rewrite it to add or remove an address.</summary>
    public string AllowedGoogleEmailsFile => Path.Combine(Dir, "allowed-google-emails");
    /// <summary>Every log line the host writes, formatted.</summary>
    public LogCapture Logs { get; } = new();

    /// <param name="configured">Fill every value Settings.PublicMissing() asks for.</param>
    /// <param name="dataDir">Reuse another harness's data directory; null means this harness's own folder.</param>
    /// <param name="dataKey">The data key; null means <see cref="DataKey"/>.</param>
    /// <param name="listenerOf">Replaces the all-public classifier, e.g. to reach the tailnet side of the same app.</param>
    /// <param name="writes">UNIFI_ALLOW_WRITES: on as the owner runs it, off by default.</param>
    /// <param name="configure">Runs on the builder after the harness's own setup (extra services, log levels).</param>
    /// <param name="signIn">MCP_SIGNIN. In code mode no Google value or allow-list is set at all.</param>
    /// <param name="accessToken">MCP_ACCESS_TOKEN, or none.</param>
    /// <param name="settings">Last changes to the settings before the host is built (e.g. turning the setup page on).</param>
    public static async Task<PublicHarness> StartAsync(bool configured = true, string? dataDir = null, string? dataKey = null,
        Func<HttpContext, Listener>? listenerOf = null, bool writes = false, Action<WebApplicationBuilder>? configure = null,
        SignInMode signIn = SignInMode.Google, string? accessToken = null, Func<Settings, Settings>? settings = null)
    {
        var h = new PublicHarness();
        var s = Harness.NewSettings(h.Dir, writes);
        s = s with { GoogleAuthority = new Uri(FakeGoogle.Authority), AllowedGoogleEmailsFile = h.AllowedGoogleEmailsFile, SignIn = signIn, AccessToken = accessToken };
        if (configured)
        {
            s = s with
            {
                PublicBaseUrl = BaseAddress,
                DataKey = dataKey ?? DataKey,
                DataDir = dataDir ?? Path.Combine(h.Dir, "data"),
            };
            if (signIn == SignInMode.Google)
            {
                File.WriteAllText(h.AllowedGoogleEmailsFile, GoogleEmail + "\n");
                s = s with { GoogleClientId = h.Google.ClientId, GoogleClientSecret = h.Google.ClientSecret };
            }
        }
        if (settings is not null) s = settings(s);
        h.Settings = s;
        h.App = McpHost.Build(s, h.Unifi, b =>
        {
            b.WebHost.UseTestServer();
            b.Logging.AddProvider(h.Logs);
            b.Services.AddSingleton<System.TimeProvider>(h.TimeProvider);
            configure?.Invoke(b);
        }, listenerOf ?? (_ => Listener.Public), h.Google);
        await h.App.StartAsync();
        h.Cookies = new CookieJar { InnerHandler = h.App.GetTestServer().CreateHandler() };
        h.Http = new HttpClient(h.Cookies) { BaseAddress = BaseAddress };
        h.Http.DefaultRequestHeaders.Host = Host;
        return h;
    }

    /// <summary>What one trip through Google sign-in saw.</summary>
    /// <param name="GoogleRedirect">Where the server sent the browser: Google's authorize URL.</param>
    /// <param name="Callback">The server's answer to Google's redirect back to /signin-google.</param>
    public sealed record SignIn(Uri GoogleRedirect, HttpResponseMessage Callback);

    /// <summary>
    /// Signs the browser in as <paramref name="email"/>: GET <paramref name="start"/> (an owner page: /connections, or the
    /// /oauth/authorize URL a client sent the browser to), follow the redirect to Google, approve there, and GET the
    /// callback Google redirects back to. An admitted sign-in's callback redirects to <paramref name="start"/> again.
    /// </summary>
    public async Task<SignIn> SignInAsync(string email, bool verified = true, bool verifiedAsString = false, string start = "/connections")
    {
        var first = await Http.GetAsync(start);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, first.StatusCode);
        var google = first.Headers.Location!;
        Assert.StartsWith(FakeGoogle.AuthorizeEndpoint + "?", google.ToString());

        Google.Approve(email, verified, verifiedAsString);
        var code = Google.Authorize(google);
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(google.Query);
        var redirectUri = new Uri(q["redirect_uri"].ToString());
        Assert.Equal(new Uri(BaseAddress, "/signin-google"), redirectUri);
        var callback = await Http.GetAsync(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(redirectUri.ToString(),
            new Dictionary<string, string?> { ["code"] = code, ["state"] = q["state"].ToString(), ["scope"] = "openid email profile" }));
        return new SignIn(google, callback);
    }

    /// <summary>The newest sign-in code the host wrote to its log (code mode), e.g. "01234-56789".</summary>
    public string LatestSignInCode() =>
        System.Text.RegularExpressions.Regex.Match(Assert.Single(Logs.Lines.Where(l => l.StartsWith("Sign-in code ", StringComparison.Ordinal)).TakeLast(1)),
            @"\b\d{5}-\d{5}\b").Value;

    /// <summary>
    /// Code mode's browser sign-in: GET <paramref name="start"/> (an owner page), follow the redirect to the code page,
    /// read the code from the log and post it. Returns the answer to the post; an accepted code redirects to <paramref name="start"/>.
    /// </summary>
    public async Task<HttpResponseMessage> CodeSignInAsync(string start = "/connections", Func<string, string>? alter = null)
    {
        var first = await Http.GetAsync(start);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, first.StatusCode);
        var page = first.Headers.Location!;
        Assert.StartsWith(CodeSignIn.Path + "?", page.OriginalString);
        var form = await Http.GetAsync(page);
        Assert.Equal(System.Net.HttpStatusCode.OK, form.StatusCode);
        var returnUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(page.OriginalString.Split('?', 2)[1])["returnUrl"].ToString();
        return await PostCodeAsync(alter is null ? LatestSignInCode() : alter(LatestSignInCode()), returnUrl);
    }

    /// <summary>Posts a sign-in code from this browser, as the code page's form does.</summary>
    public async Task<HttpResponseMessage> PostCodeAsync(string code, string returnUrl = "/connections", string? ip = null)
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, CodeSignIn.Path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["returnUrl"] = returnUrl }),
        };
        post.Headers.Add("Origin", BaseAddress.GetLeftPart(UriPartial.Authority));
        if (ip is not null) post.Headers.Add("CF-Connecting-IP", ip);
        return await Http.SendAsync(post);
    }

    /// <summary>A client of this host with no cookies: what Claude's servers use for /mcp and the token endpoint.</summary>
    public HttpClient NewClient()
    {
        var client = new HttpClient(App.GetTestServer().CreateHandler()) { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Host = Host;
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        // oauth.db lives under Dir; pooled SQLite connections would keep it open (and undeletable on Windows).
        SqliteConnection.ClearAllPools();
        Directory.Delete(Dir, recursive: true);
    }
}
