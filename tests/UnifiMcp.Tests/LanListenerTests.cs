using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace UnifiMcp.Tests;

/// <summary>MCP_LAN_LISTEN: an opt-in, token-only listener for MCP clients elsewhere on the local network.</summary>
public sealed class LanListenerTests
{
    private const string Token = "lan-token-0123456789abcdef0123456789ab";
    private const string PublicToken = "public-token-0123456789abcdef01234567";
    private const string ListenerHeader = "X-Test-Listener";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        k => values.FirstOrDefault(v => v.Name == k).Value;

    // ---- Settings ----------------------------------------------------------------------------------------------------

    [Fact]
    public void It_is_off_unless_asked_for()
    {
        var s = Settings.FromEnvironment(_ => null);
        Assert.Null(s.LanListen);
        Assert.Null(s.LanAccessToken);
        // A token alone turns nothing on.
        Assert.Null(Settings.FromEnvironment(Env(("MCP_LAN_ACCESS_TOKEN", Token))).LanAccessToken);
    }

    [Fact]
    public void It_takes_an_address_and_a_token_and_never_prints_the_token()
    {
        var s = Settings.FromEnvironment(Env(("MCP_LAN_LISTEN", "http://0.0.0.0:7420"), ("MCP_LAN_ACCESS_TOKEN", Token)));
        Assert.Equal("http://0.0.0.0:7420", s.LanListen);
        Assert.Equal(Token, s.LanAccessToken);
        Assert.DoesNotContain(Token, s.ToString());
        Assert.Contains("LAN http://0.0.0.0:7420", Harness.Summary(s, 5));
        Assert.DoesNotContain(Token, Harness.Summary(s, 5));
    }

    [Fact]
    public void Without_a_token_it_refuses_to_start()
    {
        var e = Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(Env(("MCP_LAN_LISTEN", "http://0.0.0.0:7420"))));
        Assert.Contains("MCP_LAN_ACCESS_TOKEN", e.Message);
    }

    [Fact]
    public void A_short_token_is_refused() =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(Env(("MCP_LAN_LISTEN", "http://0.0.0.0:7420"), ("MCP_LAN_ACCESS_TOKEN", "too-short"))));

    [Fact]
    public void The_lan_token_cannot_be_the_public_one() =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(Env(
            ("MCP_LAN_LISTEN", "http://0.0.0.0:7420"), ("MCP_LAN_ACCESS_TOKEN", Token), ("MCP_ACCESS_TOKEN", Token))));

    [Theory]
    [InlineData("http://server.example.com:7420")]
    [InlineData("https://0.0.0.0:7420")]
    [InlineData("http://0.0.0.0")]
    [InlineData("http://0.0.0.0:7420/mcp")]
    [InlineData("unix:/run/lan.sock")]
    public void Bad_addresses_are_refused(string listen) =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(Env(("MCP_LAN_LISTEN", listen), ("MCP_LAN_ACCESS_TOKEN", Token))));

    [Theory]
    [InlineData("MCP_LISTEN", "http://127.0.0.1:7420")]
    [InlineData("MCP_PUBLIC_LISTEN", "http://127.0.0.1:7420")]
    [InlineData("MCP_SETUP_LISTEN", "http://127.0.0.1:7420")]
    public void It_needs_a_port_of_its_own(string other, string value) =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(Env(
            ("MCP_LAN_LISTEN", "http://0.0.0.0:7420"), ("MCP_LAN_ACCESS_TOKEN", Token), (other, value))));

    [Fact]
    public void Its_port_maps_to_the_lan_listener()
    {
        var s = new Settings { LanListen = "http://0.0.0.0:7420", LanAccessToken = Token };
        Assert.Equal(Listener.Lan, Listeners.Of(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 7420), s));
        Assert.Equal(Listener.Unknown, Listeners.Of(new IPEndPoint(IPAddress.Loopback, 7420), new Settings()));
    }

    // ---- Requests ----------------------------------------------------------------------------------------------------

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required string Dir { get; init; }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Directory.Delete(Dir, recursive: true);
        }
    }

    /// <summary>No connector values at all: the LAN listener must not depend on them. A header picks the public side.</summary>
    private static async Task<Host> StartAsync(Func<Settings, Settings>? tweak = null)
    {
        var dir = Directory.CreateTempSubdirectory("unifi-mcp-lan").FullName;
        var s = Harness.NewSettings(dir) with { LanListen = "http://0.0.0.0:7420", LanAccessToken = Token, AccessToken = PublicToken };
        if (tweak is not null) s = tweak(s);
        var app = McpHost.Build(s, new FakeUnifi(), b => b.WebHost.UseTestServer(),
            ctx => ctx.Request.Headers[ListenerHeader].ToString() == "public" ? Listener.Public : Listener.Lan);
        await app.StartAsync();
        return new Host { App = app, Http = app.GetTestClient(), Dir = dir };
    }

    private static HttpRequestMessage InitializeRequest(Action<HttpRequestMessage>? change = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        r.Headers.Accept.ParseAdd("application/json");
        r.Headers.Accept.ParseAdd("text/event-stream");
        change?.Invoke(r);
        return r;
    }

    private static async Task<HttpStatusCode> SendAsync(Host h, Action<HttpRequestMessage>? change = null) =>
        (await h.Http.SendAsync(InitializeRequest(change))).StatusCode;

    [Fact]
    public async Task The_token_as_a_bearer_reaches_mcp_without_any_connector_setup()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, await SendAsync(h, r => r.Headers.Add("Authorization", "Bearer " + Token)));
    }

    [Fact]
    public async Task A_trailing_slash_is_forgiven()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, await SendAsync(h, r =>
        {
            r.RequestUri = new Uri("/mcp/", UriKind.Relative);
            r.Headers.Add("Authorization", "Bearer " + Token);
        }));
        Assert.Equal("ok", await h.Http.GetStringAsync("/healthz/"));
        using var other = new HttpRequestMessage(HttpMethod.Get, "/mcp/icon-32.png");
        other.Headers.Add("Authorization", "Bearer " + Token);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.SendAsync(other)).StatusCode);
    }

    [Fact]
    public async Task An_empty_allow_list_is_not_a_warning_when_the_lan_listener_is_on()
    {
        var logs = new LogCapture();
        var dir = Directory.CreateTempSubdirectory("unifi-mcp-lan").FullName;
        var s = Harness.NewSettings(dir) with { LanListen = "http://0.0.0.0:7420", LanAccessToken = Token, AllowedLoginsValue = null };
        File.WriteAllText(s.AllowedLoginsFile, "");
        await using (var app = McpHost.Build(s, new FakeUnifi(), b => { b.WebHost.UseTestServer(); b.Logging.AddProvider(logs); }, _ => Listener.Lan))
        {
            await app.StartAsync();
            await app.StopAsync();
        }
        Directory.Delete(dir, recursive: true);
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Warning && e.Line.Contains("every caller is refused"));
        Assert.Contains(logs.Lines, l => l.Contains("the LAN listener takes its token instead"));
    }

    [Fact]
    public async Task The_token_in_x_api_key_reaches_mcp()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, await SendAsync(h, r => r.Headers.Add("X-Api-Key", Token)));
    }

    [Fact]
    public async Task Tools_answer_over_it()
    {
        await using var h = await StartAsync();
        h.Http.DefaultRequestHeaders.Add("Authorization", "Bearer " + Token);
        await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(new ModelContextProtocol.Client.HttpClientTransport(
            new ModelContextProtocol.Client.HttpClientTransportOptions
            {
                Endpoint = new Uri(h.Http.BaseAddress!, "/mcp"),
                TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
            }, h.Http, loggerFactory: null, ownsHttpClient: false));
        Assert.Contains(await client.ListToolsAsync(), t => t.Name == "getting_started");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer lan-token-0123456789abcdef0123456789a")]   // one character short
    [InlineData("Bearer LAN-TOKEN-0123456789ABCDEF0123456789AB")]  // case matters
    [InlineData("Bearer " + PublicToken)]                          // the public token is not the LAN one
    public async Task Anything_but_the_lan_token_is_401(string? authorization)
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(h, r =>
        {
            if (authorization is not null) r.Headers.Add("Authorization", authorization);
        }));
    }

    [Fact]
    public async Task The_tailscale_header_means_nothing_here()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(h, r => r.Headers.Add(CallerPolicy.LoginHeader, Harness.Login)));
    }

    [Fact]
    public async Task The_lan_token_never_opens_the_public_side()
    {
        // Configured, so the public side answers rather than 503: it still refuses the LAN token.
        await using var h = await StartAsync(s => s with
        {
            PublicBaseUrl = new Uri("https://unifi.example.com"), SignIn = SignInMode.Code, DataKey = "data-key-0123456789abcdef",
            DataDir = Path.Combine(Path.GetTempPath(), "unifi-mcp-lan-data-" + Guid.NewGuid().ToString("N")),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(h, r =>
        {
            r.Headers.Host = "unifi.example.com";
            r.Headers.Add(ListenerHeader, "public");
            r.Headers.Add("Authorization", "Bearer " + Token);
        }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(h, r =>
        {
            r.Headers.Host = "unifi.example.com";
            r.Headers.Add(ListenerHeader, "public");
            r.Headers.Add("Authorization", "Bearer " + PublicToken);
        }));
    }

    [Theory]
    [InlineData("/oauth/authorize")]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/connections")]
    [InlineData("/signin-code")]
    [InlineData("/setup")]
    [InlineData("/mcp/icon-32.png")]
    public async Task It_serves_mcp_and_the_health_check_and_nothing_else(string path)
    {
        await using var h = await StartAsync();
        using var r = new HttpRequestMessage(HttpMethod.Get, path);
        r.Headers.Add("Authorization", "Bearer " + Token);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.SendAsync(r)).StatusCode);
        Assert.Equal("ok", await h.Http.GetStringAsync("/healthz"));
    }

    [Fact]
    public async Task Browsers_are_refused_as_on_the_sockets()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(h, r =>
        {
            r.Headers.Add("Authorization", "Bearer " + Token);
            r.Headers.Add("Origin", "http://evil.example");
        }));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await SendAsync(h, r =>
        {
            r.Headers.Add("Authorization", "Bearer " + Token);
            r.Content = new StringContent(Initialize, Encoding.UTF8, "text/plain");
        }));
    }

    // ---- The real server -----------------------------------------------------------------------------------------

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task The_real_server_binds_it_next_to_its_sockets()
    {
        var dir = Directory.CreateTempSubdirectory("umcp-lan").FullName;
        var port = FreePort();
        var settings = Harness.NewSettings(dir) with
        {
            Listen = "unix:" + Path.Combine(dir, "t.sock"), PublicListen = "unix:" + Path.Combine(dir, "p.sock"),
            LanListen = $"http://127.0.0.1:{port}", LanAccessToken = Token,
        };
        await using var app = McpHost.Build(settings);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Assert.Equal("ok", await http.GetStringAsync("/healthz"));
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(InitializeRequest(r => r.Headers.Add("Authorization", "Bearer " + Token)))).StatusCode);
        // Classified as LAN, not as the tailnet: the login header alone gets nowhere.
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(InitializeRequest(r => r.Headers.Add(CallerPolicy.LoginHeader, Harness.Login)))).StatusCode);
        await app.StopAsync();
    }

    // ---- --healthcheck -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://0.0.0.0:5199", "http://127.0.0.1:5199/healthz")]
    [InlineData("http://*:5199;http://[::]:5200", "http://127.0.0.1:5199/healthz")]
    [InlineData("http://[::]:5199", "http://[::1]:5199/healthz")]
    [InlineData("http://127.0.0.1:5199", "http://127.0.0.1:5199/healthz")]
    [InlineData("unix:/run/unifi-mcp/mcp.sock", "http://localhost/healthz")]
    public void The_health_check_asks_the_tailnet_listener_from_inside(string listen, string url) =>
        Assert.Equal(url, HealthCheck.Target(listen).Url.ToString());

    [Fact]
    public async Task The_health_check_passes_while_the_server_runs_and_fails_once_it_stops()
    {
        var dir = Directory.CreateTempSubdirectory("umcp-hc").FullName;
        var sock = Path.Combine(dir, "t.sock");
        // Not configured (no key, no pin): still healthy, so the setup page and the tunnel stay reachable.
        var settings = Harness.NewSettings(dir, key: false, pin: false) with { Listen = "unix:" + sock, PublicListen = "unix:" + Path.Combine(dir, "p.sock") };
        var env = Env(("MCP_LISTEN", "unix:" + sock), ("MCP_SETUP_PAGE", "off"));
        await using (var app = McpHost.Build(settings))
        {
            await app.StartAsync();
            var output = new StringWriter();
            Assert.Equal(0, await HealthCheck.RunAsync(env, output));
            Assert.Equal("ok", output.ToString().Trim());
            await app.StopAsync();
        }
        var after = new StringWriter();
        Assert.Equal(1, await HealthCheck.RunAsync(env, after));
        Assert.StartsWith("unhealthy:", after.ToString());
    }

    [Fact]
    public async Task Settings_that_cannot_start_the_server_are_unhealthy()
    {
        var output = new StringWriter();
        Assert.Equal(1, await HealthCheck.RunAsync(Env(("MCP_LAN_LISTEN", "http://0.0.0.0:7420")), output));
        Assert.Contains("MCP_LAN_ACCESS_TOKEN", output.ToString());
    }
}
