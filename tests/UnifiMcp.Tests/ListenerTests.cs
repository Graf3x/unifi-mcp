using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace UnifiMcp.Tests;

public class ListenerTests
{
    private static readonly Settings Defaults = new();

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    [Fact]
    public void Unix_paths_map_to_their_listener()
    {
        Assert.Equal(Listener.Public, Listeners.Of(new UnixDomainSocketEndPoint("/run/unifi-mcp/public.sock"), Defaults));
        Assert.Equal(Listener.Tailnet, Listeners.Of(new UnixDomainSocketEndPoint("/run/unifi-mcp/mcp.sock"), Defaults));
        Assert.Equal(Listener.Unknown, Listeners.Of(new UnixDomainSocketEndPoint("/x.sock"), Defaults));
    }

    [Fact]
    public void Tcp_ports_map_to_their_listener()
    {
        var s = Defaults with { Listen = "http://127.0.0.1:5000", PublicListen = "http://127.0.0.1:5001" };
        Assert.Equal(Listener.Tailnet, Listeners.Of(new IPEndPoint(IPAddress.Loopback, 5000), s));
        Assert.Equal(Listener.Public, Listeners.Of(new IPEndPoint(IPAddress.Loopback, 5001), s));
        Assert.Equal(Listener.Unknown, Listeners.Of(new IPEndPoint(IPAddress.Loopback, 5002), s));
    }

    [Fact]
    public void Null_endpoint_is_unknown() => Assert.Equal(Listener.Unknown, Listeners.Of(null, Defaults));

    [Fact]
    public async Task Unknown_listener_gets_403()
    {
        await using var h = await Harness.StartAsync(login: null, listenerOf: _ => Listener.Unknown);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Tailscale_header_on_public_without_token_is_refused()
    {
        await using var h = await PublicHarness.StartAsync();
        h.Http.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Harness.Login);
        var response = await h.Http.PostAsync("/mcp", new StringContent(Initialize, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Listener TailnetIfMarked(HttpContext ctx) =>
        ctx.Request.Headers.ContainsKey("X-Test-Tailnet") ? Listener.Tailnet : Listener.Public;

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method == "POST" ? new StringContent("{}", Encoding.UTF8, "application/json") : null,
    };

    /// <summary>
    /// Against a Ready connector, so every route is mapped: the tailnet's 404 is the gate's. Where the public side has
    /// the route, the same request there shows it does.
    /// </summary>
    [Theory]
    [InlineData("GET", "/connections", true)]
    [InlineData("GET", "/signin-google", true)]
    [InlineData("GET", "/oauth/authorize", true)]
    [InlineData("POST", "/oauth/token", true)]
    [InlineData("POST", "/oauth/register", true)]
    [InlineData("GET", "/.well-known/oauth-authorization-server", true)]
    [InlineData("GET", "/.well-known/oauth-authorization-server/mcp", false)]
    public async Task Oauth_routes_do_not_exist_on_the_tailnet(string method, string path, bool routedOnPublic)
    {
        await using var h = await PublicHarness.StartAsync(listenerOf: TailnetIfMarked);
        Assert.True(h.App.Services.GetRequiredService<ConnectorState>().Ready);

        using var tailnet = h.NewClient();
        tailnet.DefaultRequestHeaders.Add("X-Test-Tailnet", "1");
        tailnet.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Harness.Login);
        using (var request = Request(method, path))
            Assert.Equal(HttpStatusCode.NotFound, (await tailnet.SendAsync(request)).StatusCode);

        if (!routedOnPublic) return;
        using var internet = h.NewClient();
        using (var request = Request(method, path))
            Assert.NotEqual(HttpStatusCode.NotFound, (await internet.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// The tailnet serves the MCP endpoint, the health check and the icons, and nothing else of the public side's: not the
    /// resource metadata (tailnet callers are identified by Tailscale, not OAuth) and not the sign-in handler's paths.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/signout-oidc")]
    [InlineData("/signout-callback-oidc")]
    [InlineData("/connectionsX")]
    [InlineData("/setup")]
    [InlineData("/admin")]
    public async Task Tailnet_serves_nothing_else_of_the_public_side(string path)
    {
        await using var h = await PublicHarness.StartAsync(listenerOf: TailnetIfMarked);
        using var tailnet = h.NewClient();
        tailnet.DefaultRequestHeaders.Add("X-Test-Tailnet", "1");
        tailnet.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Harness.Login);
        Assert.Equal(HttpStatusCode.NotFound, (await tailnet.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/healthz/")]
    [InlineData("/mcp/icon-512.png")]
    [InlineData("/mcp/icon-180.png")]
    [InlineData("/mcp/icon-32.png")]
    public async Task Tailnet_serves_its_health_check_and_the_icons(string path)
    {
        await using var h = await PublicHarness.StartAsync(listenerOf: TailnetIfMarked);
        using var tailnet = h.NewClient();
        tailnet.DefaultRequestHeaders.Add("X-Test-Tailnet", "1");
        Assert.Equal(HttpStatusCode.OK, (await tailnet.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Tailnet_tools_work_beside_a_ready_connector()
    {
        // Stage 2 in production: the connector fully configured, Claude Code still on the tailnet socket.
        await using var h = await PublicHarness.StartAsync(listenerOf: _ => Listener.Tailnet);
        Assert.True(h.App.Services.GetRequiredService<ConnectorState>().Ready);
        Assert.Contains("connector ready", Assert.Single(h.Logs.Lines, l => l.StartsWith("unifi-mcp on ", StringComparison.Ordinal)));

        var http = h.NewClient();
        http.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Harness.Login);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(PublicHarness.BaseAddress, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "unifi-mcp-tests",
        }, http, loggerFactory: null, ownsHttpClient: true));
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "getting_started");
    }

    [Fact]
    public async Task Wrong_host_on_public_gets_421()
    {
        await using var h = await PublicHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await h.Http.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("UNIFI.example.com")]
    [InlineData("unifi.example.com:443")]
    public async Task Public_host_match_ignores_case_and_default_port(string host)
    {
        await using var h = await PublicHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = host;
        Assert.Equal(HttpStatusCode.OK, (await h.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Public_urls_name_the_canonical_host_whatever_host_was_sent()
    {
        await using var h = await PublicHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Host = "UNIFI.example.com:443";
        var response = await h.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=\"https://unifi.example.com/.well-known/oauth-protected-resource", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Public_answers_503_until_configured()
    {
        await using var h = await PublicHarness.StartAsync(configured: false);
        var response = await h.Http.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("The connector isn't set up yet.", await response.Content.ReadAsStringAsync());

        await using var tailnet = await Harness.StartAsync();
        var (_, isError) = await tailnet.CallAsync("getting_started");
        Assert.False(isError);
    }

    [Fact]
    public async Task Public_post_from_foreign_origin_is_refused()
    {
        await using var h = await PublicHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new StringContent("grant_type=authorization_code", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_real_server_tells_its_two_unix_sockets_apart()
    {
        var dir = Directory.CreateTempSubdirectory("umcp2").FullName;
        var (tail, pub) = (Path.Combine(dir, "t.sock"), Path.Combine(dir, "p.sock"));
        var settings = Harness.NewSettings(dir) with { Listen = "unix:" + tail, PublicListen = "unix:" + pub };
        await using var app = McpHost.Build(settings);
        await app.StartAsync();
        HttpClient Over(string sock) => new(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(sock), ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        }) { BaseAddress = new Uri("http://localhost") };
        using var tailnet = Over(tail);
        using var internet = Over(pub);
        Assert.Equal("ok", await tailnet.GetStringAsync("/healthz"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await internet.GetAsync("/healthz")).StatusCode); // public, not configured
        await app.StopAsync();
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/MCP")]
    public async Task Public_mcp_keeps_the_strict_origin_rule_in_any_case(string path)
    {
        await using var h = await PublicHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "https://" + PublicHarness.Host);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Public_serves_only_its_own_paths()
    {
        await using var h = await PublicHarness.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Public_forgives_a_trailing_slash_as_the_other_listeners_do()
    {
        await using var h = await PublicHarness.StartAsync();
        Assert.Equal("ok", await h.Http.GetStringAsync("/healthz/"));
        var response = await h.Http.PostAsync("/mcp/", new StringContent(Initialize, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A path that only starts like a route is refused at the door, before the origin check would answer 403: the gate
    /// knows which paths exist, so it is not routing's 404.
    /// </summary>
    [Theory]
    [InlineData("/connectionsX")]
    [InlineData("/mcp/icon-evil.png")]
    [InlineData("/.well-known/security.txt")]
    [InlineData("/oauth/other")]
    public async Task Public_refuses_a_look_alike_path_at_the_door(string path)
    {
        await using var h = await PublicHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.NotFound, (await h.Http.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// What an MCP client's discovery asks for, and what it gets: the four documents that exist, and the forms that do
    /// not (path-suffixed authorization-server metadata, other well-known documents) a 404, so clients fall back as before.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource", HttpStatusCode.OK)]
    [InlineData("/.well-known/oauth-protected-resource/mcp", HttpStatusCode.OK)]
    [InlineData("/.well-known/oauth-authorization-server", HttpStatusCode.OK)]
    [InlineData("/.well-known/openid-configuration", HttpStatusCode.OK)]
    [InlineData("/.well-known/oauth-authorization-server/mcp", HttpStatusCode.NotFound)]
    [InlineData("/.well-known/openid-configuration/mcp", HttpStatusCode.NotFound)]
    [InlineData("/mcp/.well-known/openid-configuration", HttpStatusCode.NotFound)]
    [InlineData("/.well-known/mcp", HttpStatusCode.NotFound)]
    [InlineData("/.well-known/mcp.json", HttpStatusCode.NotFound)]
    public async Task Public_discovery_paths_answer_as_clients_expect(string path, HttpStatusCode status)
    {
        await using var h = await PublicHarness.StartAsync();
        using var client = h.NewClient();
        Assert.Equal(status, (await client.GetAsync(path)).StatusCode);
    }
}
