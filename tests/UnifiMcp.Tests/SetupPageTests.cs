using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace UnifiMcp.Tests;

/// <summary>The setup page: its own listener, a key from the log traded for a cookie, the live code, and the checks.</summary>
public sealed partial class SetupPageTests
{
    private const string ListenerHeader = "X-Test-Listener";
    private const string CertHex = "C0FFEE0123456789C0FFEE0123456789C0FFEE0123456789C0FFEE0123456789";
    private const string OtherKey = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string SetupOrigin = "http://localhost:5198";

    private static Listener Classify(HttpContext ctx) => ctx.Request.Headers[ListenerHeader].ToString() switch
    {
        "setup" => Listener.Setup,
        "tailnet" => Listener.Tailnet,
        _ => Listener.Public,
    };

    private sealed class FakeProbe : ICertificateProbe
    {
        public ProbedCertificate Answer { get; set; } = new(CertHex, Harness.Pin, SslPolicyErrors.RemoteCertificateChainErrors, 3);
        public string? Fail { get; set; }

        public Task<ProbedCertificate> ProbeAsync(Uri console, CancellationToken ct) =>
            Fail is not null ? throw new ProbeFailed(Fail) : Task.FromResult(Answer);
    }

    private sealed class FakeOutbound : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required PublicHarness Host { get; init; }
        public required HttpClient Http { get; init; }
        public required CookieJar Jar { get; init; }
        public required FakeProbe Probe { get; init; }
        public required FakeOutbound Outbound { get; init; }

        public string Key => KeyIn().Match(Assert.Single(Host.Logs.Lines, l => l.StartsWith("Setup page: ", StringComparison.Ordinal))).Groups[1].Value;

        public async Task UnlockAsync()
        {
            var r = await Http.GetAsync("/setup?key=" + Key);
            Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        }

        public Task<HttpResponseMessage> PostAsync(string path, object? body = null, string? origin = SetupOrigin)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
            if (origin is not null) req.Headers.Add("Origin", origin);
            return Http.SendAsync(req);
        }

        public async Task<JsonElement> CheckAsync(string id)
        {
            var r = await PostAsync("/setup/api/check/" + id);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>());
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Host.DisposeAsync();
        }
    }

    private static async Task<Setup> StartAsync(SignInMode signIn = SignInMode.Code, bool configured = true, Func<Settings, Settings>? tweak = null)
    {
        var probe = new FakeProbe();
        var outbound = new FakeOutbound();
        var host = await PublicHarness.StartAsync(signIn: signIn, configured: configured, listenerOf: Classify,
            settings: s => (tweak ?? (x => x))(s with { SetupPage = true }),
            configure: b =>
            {
                b.Services.AddSingleton<ICertificateProbe>(probe);
                b.Services.AddKeyedSingleton<HttpMessageHandler>(SetupChecks.OutboundKey, outbound);
            });
        host.Unifi.WithSite().Route("GET", "/v1/info", """{"applicationVersion":"9.9.9"}""");
        var jar = new CookieJar { InnerHandler = host.App.GetTestServer().CreateHandler() };
        var http = new HttpClient(jar) { BaseAddress = new Uri(SetupOrigin) };
        http.DefaultRequestHeaders.Add(ListenerHeader, "setup");
        return new Setup { Host = host, Http = http, Jar = jar, Probe = probe, Outbound = outbound };
    }

    [GeneratedRegex(@"\?key=([A-Za-z0-9_-]+)")]
    private static partial Regex KeyIn();

    // ---- Settings and listeners ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null, false, true)]   // guide 1: nothing set
    [InlineData(null, null, true, false)]   // the server's own default sign-in is google
    [InlineData("code", null, true, true)] // guide 2
    [InlineData("google", null, true, false)] // guide 3
    [InlineData("google", "on", true, true)]
    [InlineData("code", "off", true, false)]
    [InlineData(null, "true", true, true)]
    public void The_page_is_on_except_in_the_full_google_setup(string? signIn, string? page, bool publicUrl, bool expected)
    {
        var s = Settings.FromEnvironment(k => k switch
        {
            "MCP_SIGNIN" => signIn,
            "MCP_SETUP_PAGE" => page,
            "MCP_PUBLIC_BASE_URL" => publicUrl ? "https://unifi.example.com" : null,
            _ => null,
        });
        Assert.Equal(expected, s.SetupPage);
        Assert.Equal(Settings.DefaultSetupListen, s.SetupListen);
    }

    [Theory]
    [InlineData("MCP_SETUP_PAGE", "maybe")]
    [InlineData("MCP_SETUP_LISTEN", "http://setup.example.com:5198")]
    [InlineData("MCP_SETUP_LISTEN", "https://127.0.0.1:5198")]
    [InlineData("MCP_SETUP_LISTEN", "http://127.0.0.1")]
    [InlineData("MCP_SETUP_LISTEN", "http://127.0.0.1:5198/setup")]
    [InlineData("MCP_SETUP_LISTEN", "unix:/run/setup.sock")]
    public void Bad_setup_values_are_refused(string name, string value) =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == name ? value : null));

    [Fact]
    public void The_setup_port_cannot_be_another_listeners()
    {
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k switch
        {
            "MCP_LISTEN" => "http://127.0.0.1:5198",
            _ => null,
        }));
        // Off, the port is not bound, so it may be anything.
        Assert.False(Settings.FromEnvironment(k => k switch { "MCP_LISTEN" => "http://127.0.0.1:5198", "MCP_SETUP_PAGE" => "off", _ => null }).SetupPage);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5198")]
    [InlineData("http://[::]:5198")]
    [InlineData("http://localhost:5198")]
    [InlineData("http://192.168.1.10:5198")]
    public void Wildcards_loopback_and_addresses_are_accepted(string listen) =>
        Assert.Equal(5198, Settings.FromEnvironment(k => k == "MCP_SETUP_LISTEN" ? listen : null).SetupListen is var l ? Listeners.SetupEndpoint(l)!.Value.Port : 0);

    [Fact]
    public void The_setup_port_is_the_setup_listener_only_when_the_page_is_on()
    {
        var on = new Settings { SetupPage = true };
        Assert.Equal(Listener.Setup, Listeners.Of(new IPEndPoint(IPAddress.Loopback, 5198), on));
        Assert.Equal(Listener.Unknown, Listeners.Of(new IPEndPoint(IPAddress.Loopback, 5198), on with { SetupPage = false }));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5198", "http://localhost:5198/setup?key=")]
    [InlineData("http://127.0.0.1:5198", "http://127.0.0.1:5198/setup?key=")]
    [InlineData("http://[::1]:6000", "http://[::1]:6000/setup?key=")]
    public void The_logged_link_is_one_a_browser_on_the_host_can_open(string listen, string start) =>
        Assert.StartsWith(start, SetupPage.Link(new Settings { SetupListen = listen }, new SetupKey()));

    // ---- Access ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_key_the_page_is_locked_and_shows_no_code()
    {
        await using var s = await StartAsync();
        await s.Host.Http.GetAsync(CodeSignIn.Path); // a live code exists
        var r = await s.Http.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("Open this page from the server", html);
        Assert.DoesNotContain(s.Host.LatestSignInCode(), html);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.PostAsync("/setup/api/code")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.PostAsync("/setup/api/check/console")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        await using var s = await StartAsync();
        var r = await s.Http.GetAsync("/setup?key=" + new string('A', 32));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(s.Jar.SetCookies);
    }

    [Fact]
    public async Task The_key_becomes_a_strict_cookie_and_leaves_the_address_bar()
    {
        await using var s = await StartAsync();
        var r = await s.Http.GetAsync("/setup?key=" + s.Key);
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/setup", r.Headers.Location!.OriginalString);
        var cookie = s.Jar.LastSetCookie(SetupPage.CookieName)!.ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/setup", cookie);

        var page = await s.Http.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.Contains("Checks", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_the_setup_listener_serves_the_page_and_it_serves_nothing_else()
    {
        await using var s = await StartAsync();
        var key = s.Key;
        // The public side (what the tunnel reaches) and the tailnet side never answer /setup.
        Assert.Equal(HttpStatusCode.NotFound, (await s.Host.Http.GetAsync("/setup?key=" + key)).StatusCode);
        var tailnet = new HttpRequestMessage(HttpMethod.Get, "/setup?key=" + key);
        tailnet.Headers.Add(ListenerHeader, "tailnet");
        tailnet.Headers.Add(CallerPolicy.LoginHeader, Harness.Login);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Host.NewClient().SendAsync(tailnet)).StatusCode);
        // The setup side has no MCP, OAuth, sign-in or health routes.
        foreach (var path in new[] { "/mcp", "/connections", CodeSignIn.Path, "/oauth/authorize", "/healthz", "/.well-known/oauth-authorization-server", "/setupx" })
            Assert.Equal(HttpStatusCode.NotFound, (await s.Http.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_cookie_holds_a_session_never_the_key()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        var cookie = s.Jar.Get(new Uri(SetupOrigin + "/setup"), SetupPage.CookieName)!.Value;
        Assert.NotEqual(s.Key, cookie);
        // The key itself, sent as the cookie, opens nothing.
        var forged = new HttpRequestMessage(HttpMethod.Get, "/setup");
        forged.Headers.Add("Cookie", $"{SetupPage.CookieName}={s.Key}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().SendAsync(WithSetup(forged))).StatusCode);
    }

    [Theory]
    [InlineData("evil.example:5198")]
    [InlineData("unifi.example.com")]
    [InlineData("rebind.127.0.0.1.nip.io:5198")]
    public async Task Only_localhost_or_an_address_may_name_the_setup_page(string host)
    {
        await using var s = await StartAsync();
        var r = new HttpRequestMessage(HttpMethod.Get, "/setup?key=" + s.Key);
        r.Headers.Host = host;
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await s.Host.NewClient().SendAsync(WithSetup(r))).StatusCode);
        Assert.Empty(s.Jar.SetCookies);
    }

    [Theory]
    [InlineData("localhost:5198")]
    [InlineData("127.0.0.1:5198")]
    [InlineData("[::1]:5198")]
    [InlineData("192.168.1.20:5198")]
    public void Hosts_a_browser_on_the_machine_uses_are_allowed(string host) =>
        Assert.True(SetupPage.HostAllowed(HostString.FromUriComponent(host)));

    [Fact]
    public void Every_url_of_a_listener_counts_for_the_port_check() =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == "MCP_LISTEN" ? "http://127.0.0.1:5000;http://127.0.0.1:5198" : null));

    private static HttpRequestMessage WithSetup(HttpRequestMessage r)
    {
        r.Headers.Add(ListenerHeader, "setup");
        r.Headers.Host ??= "localhost:5198"; // NewClient's default would name the public host
        return r;
    }

    [Fact]
    public async Task Calls_from_another_origin_or_as_forms_are_refused()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await s.PostAsync("/setup/api/code", origin: "http://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.PostAsync("/setup/api/code", origin: "null")).StatusCode);
        var form = new HttpRequestMessage(HttpMethod.Post, "/setup/api/code") { Content = new FormUrlEncodedContent([]) };
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await s.Http.SendAsync(form)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.PostAsync("/setup/api/code")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.PostAsync("/setup/api/code", origin: null)).StatusCode);
    }

    // ---- The sign-in code ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_waits_for_the_code_the_sign_in_page_issues()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        var before = await (await s.PostAsync("/setup/api/code", new { @new = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("code").ValueKind);

        await s.Host.Http.GetAsync(CodeSignIn.Path);
        var after = await (await s.PostAsync("/setup/api/code", new { @new = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(s.Host.LatestSignInCode(), after.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_code_from_the_page_signs_in_once()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        var r = await s.PostAsync("/setup/api/code", new { @new = true });
        var code = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        Assert.Matches(@"^\d{5}-\d{5}$", code);
        Assert.DoesNotContain(s.Host.Logs.Lines, l => l.Contains(code, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.SeeOther, (await s.Host.PostCodeAsync(code)).StatusCode);
        var gone = await (await s.PostAsync("/setup/api/code")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("code").ValueKind);
    }

    [Fact]
    public async Task Google_sign_in_has_no_codes_to_show()
    {
        await using var s = await StartAsync(SignInMode.Google);
        await s.UnlockAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostAsync("/setup/api/code", new { @new = true })).StatusCode);
        Assert.Contains("data-codes=\"off\"", await s.Http.GetStringAsync("/setup"));
    }

    // ---- Checks ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_check_passes_on_a_working_setup_and_no_answer_carries_a_secret()
    {
        const string token = "token-0123456789-0123456789-0123456789";
        await using var s = await StartAsync(tweak: x => x with { AccessToken = token });
        await s.UnlockAsync();
        var all = new StringBuilder(await s.Http.GetStringAsync("/setup"));
        foreach (var id in SetupChecks.Ids)
        {
            var r = await s.CheckAsync(id);
            all.Append(r.GetRawText());
            Assert.True(r.GetProperty("status").GetString() == "Ok", $"{id}: {r}");
        }
        Assert.Equal([new Uri("https://unifi.example.com/healthz")], s.Outbound.Seen);
        var text = all.ToString();
        Assert.Contains("9.9.9", text);
        foreach (var secret in new[] { Harness.ApiKey, PublicHarness.DataKey, token })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task A_pin_mismatch_shows_both_fingerprints_to_compare()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        s.Probe.Answer = new(CertHex, OtherKey, SslPolicyErrors.RemoteCertificateChainErrors, 3);
        var r = await s.CheckAsync("certificate");
        Assert.Equal("Fail", r.GetProperty("status").GetString());
        var copy = r.GetProperty("copy").EnumerateArray().Select(c => c.GetProperty("value").GetString()).ToList();
        Assert.Equal([OtherKey, CertHex], copy);
    }

    [Fact]
    public async Task With_no_pin_the_presented_key_is_offered_and_the_api_key_is_not_sent()
    {
        await using var s = await StartAsync(tweak: x => x with { CertPinFile = Path.Combine(x.DataDir, "no-such-pin") });
        await s.UnlockAsync();
        var cert = await s.CheckAsync("certificate");
        Assert.Equal("Fail", cert.GetProperty("status").GetString());
        Assert.Contains("confirm it on the console", cert.GetProperty("detail").GetString());
        Assert.Contains(cert.GetProperty("copy").EnumerateArray(), c => c.GetProperty("value").GetString() == Harness.Pin);
        Assert.Equal("Blocked", (await s.CheckAsync("apikey")).GetProperty("status").GetString());
        Assert.Empty(s.Host.Unifi.Requests);
    }

    [Fact]
    public async Task A_typo_in_the_pin_is_told_apart_from_no_pin()
    {
        await using var s = await StartAsync(tweak: x => x with { CertSha256 = "not-a-fingerprint" });
        await s.UnlockAsync();
        var r = await s.CheckAsync("certificate");
        Assert.Contains("isn't a fingerprint", r.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task A_console_that_does_not_answer_holds_the_certificate_check()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        s.Probe.Fail = "No answer from 192.168.1.1:443 within 5 seconds.";
        var console = await s.CheckAsync("console");
        Assert.Equal("Fail", console.GetProperty("status").GetString());
        Assert.Equal(s.Probe.Fail, console.GetProperty("summary").GetString());
        Assert.Equal("Blocked", (await s.CheckAsync("certificate")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_refused_api_key_is_named()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        s.Host.Unifi.Route("GET", "/v1/info", """{"message":"Unauthorized"}""", 401);
        var r = await s.CheckAsync("apikey");
        Assert.Equal("Fail", r.GetProperty("status").GetString());
        Assert.Contains("refused the key", r.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task An_unknown_site_lists_the_ones_there_are()
    {
        await using var s = await StartAsync(tweak: x => x with { Site = "branch" });
        await s.UnlockAsync();
        var r = await s.CheckAsync("site");
        Assert.Equal("Fail", r.GetProperty("status").GetString());
        Assert.Contains("Default", r.GetProperty("summary").GetString());
    }

    [Theory]
    [InlineData(302, "https://team.cloudflareaccess.com/cdn-cgi/access/login", null, "Cloudflare Access")]
    [InlineData(403, null, "cf-mitigated", "challenge")]
    [InlineData(530, null, null, "can't reach the tunnel")]
    [InlineData(502, null, null, "can't reach this server")]
    [InlineData(421, null, null, "another host name")]
    [InlineData(404, null, null, "answered 404")]
    public async Task Tunnel_problems_are_named(int status, string? location, string? header, string expected)
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        s.Outbound.Respond = _ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") };
            if (location is not null) r.Headers.Location = new Uri(location);
            if (header is not null) r.Headers.Add(header, "challenge");
            return r;
        };
        var check = await s.CheckAsync("tunnel");
        Assert.Equal("Fail", check.GetProperty("status").GetString());
        Assert.Contains(expected, check.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Google_values_are_checked_and_the_redirect_uri_offered()
    {
        await using var s = await StartAsync(SignInMode.Google, tweak: x => x with { GoogleClientId = "not-a-client-id" });
        await s.UnlockAsync();
        var r = await s.CheckAsync("signin");
        Assert.Equal("Fail", r.GetProperty("status").GetString());
        Assert.Contains("apps.googleusercontent.com", r.GetProperty("detail").GetString());
        Assert.Equal("https://unifi.example.com/signin-google", r.GetProperty("copy")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_unknown_check_is_not_found()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostAsync("/setup/api/check/nope")).StatusCode);
    }

    // ---- The page ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Values_on_the_page_are_encoded()
    {
        await using var s = await StartAsync(configured: false, tweak: x => x with { AllowedLoginsValue = "<img src=x onerror=alert(1)>" });
        await s.UnlockAsync();
        var html = await s.Http.GetStringAsync("/setup");
        Assert.Contains("data-guide=\"1\"", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
    }

    [Fact]
    public async Task Diagrams_assets_and_icon_are_served_and_nothing_outside_them()
    {
        await using var s = await StartAsync();
        var svg = await s.Http.GetAsync("/setup/images/setup-paths-light.svg");
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.GetAsync("/setup/images/..%2FREADME.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.GetAsync("/setup/images/missing.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Http.GetAsync("/setup/setup.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Http.GetAsync("/setup/setup.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Http.GetAsync("/setup/icon.png")).StatusCode);
    }

    // ---- The guide ---------------------------------------------------------------------------------------------------

    private static readonly SetupGuide Guide = new(new Settings());

    [Fact]
    public void The_guide_is_the_readme_setup_section_in_parts()
    {
        var ids = Guide.Parts.Select(p => p.Id).ToList();
        Assert.Equal("intro", ids[0]);
        Assert.Contains("before-you-start-two-values-from-your-console", ids);
        Assert.Contains("guide-1-quick-local-setup", ids);
        Assert.Contains("guide-2-claudeai-with-a-sign-in-code", ids);
        Assert.Contains("guide-3-full-setup-with-google-sign-in", ids);
        var html = string.Concat(Guide.Parts.Select(p => p.Html));
        Assert.Contains("srcset=\"/setup/images/setup-paths-dark.svg\"", html);
        Assert.DoesNotContain("src=\"docs/images/", html);
        Assert.Contains($"href=\"{SetupGuide.RepoUrl}#writes\"", html); // outside the section: to GitHub
        Assert.Contains("href=\"#guide-2-claudeai-with-a-sign-in-code\"", html); // inside: stays
        Assert.DoesNotContain("<mark", html); // README values: nothing to replace
    }

    [Fact]
    public void Every_heading_the_page_points_at_exists_in_the_readme()
    {
        var html = string.Concat(Guide.Parts.Select(p => p.Html));
        var js = SetupGuide.ReadResource("setup/setup.js")!;
        var cs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "UnifiMcp", "Setup", "SetupChecks.cs"));
        var wanted = Regex.Matches(js, "\"([a-z0-9-]+)\": \\[").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(cs, "private const string \\w+ = \"([a-z0-9-]+)\";").Select(m => m.Groups[1].Value))
            .Where(id => id.Contains('-'))
            .ToList();
        Assert.NotEmpty(wanted);
        Assert.All(wanted, id => Assert.Contains($"id=\"{id}\"", html));
    }

    [Fact]
    public void Placeholders_become_this_servers_values_in_text_only()
    {
        var mine = new SetupGuide(new Settings { BaseUrl = new Uri("https://10.0.0.5"), PublicBaseUrl = new Uri("https://net.example.org") });
        var html = string.Concat(mine.Parts.Select(p => p.Html));
        Assert.Contains("<mark class=\"yours\" title=\"Your value\">net.example.org</mark>", html);
        Assert.Contains("<mark class=\"yours\" title=\"Your value\">https://10.0.0.5</mark>", html);
        Assert.Contains("<mark class=\"yours\" title=\"Your value\">10.0.0.5:443</mark>", html);
        // Attributes (image alt text) are left alone, so no mark ever lands inside a tag.
        Assert.DoesNotMatch("<[^>]*<mark", html);
    }

    [Fact]
    public void A_build_without_the_readme_has_no_parts() =>
        Assert.Empty(new SetupGuide("# Title\n\nNo setup section here.\n", new Settings()).Parts);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "unifi-mcp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
