using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;

namespace UnifiMcp.Tests;

public class CallerPolicyTests
{
    private static readonly string[] Allowed = ["owner@example.test"];

    [Fact] public void Unix_socket_with_an_allowed_login_is_allowed() =>
        Assert.Equal(CallerDecision.Allow, CallerPolicy.Evaluate(CallerTransport.UnixSocket, "OWNER@example.test", Allowed, false));

    [Fact] public void A_remote_tcp_caller_is_refused_even_with_the_header() =>
        Assert.Equal(CallerDecision.DenyUntrustedTransport, CallerPolicy.Evaluate(CallerTransport.RemoteTcp, "owner@example.test", Allowed, true));

    [Fact] public void Loopback_tcp_is_refused_unless_enabled() =>
        Assert.Equal(CallerDecision.DenyUntrustedTransport, CallerPolicy.Evaluate(CallerTransport.LoopbackTcp, "owner@example.test", Allowed, false));

    [Fact] public void No_login_is_refused() =>
        Assert.Equal(CallerDecision.DenyNoIdentity, CallerPolicy.Evaluate(CallerTransport.UnixSocket, " ", Allowed, false));

    [Fact] public void A_login_not_on_the_list_is_refused() =>
        Assert.Equal(CallerDecision.DenyNotAllowed, CallerPolicy.Evaluate(CallerTransport.UnixSocket, "someone@else.test", Allowed, false));

    [Fact] public void An_empty_list_refuses_everyone() =>
        Assert.Equal(CallerDecision.DenyNotAllowed, CallerPolicy.Evaluate(CallerTransport.UnixSocket, "owner@example.test", [], false));
}

public class EndpointTests
{
    private static StringContent Json() => new("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json");

    [Fact]
    public async Task No_identity_gets_403()
    {
        await using var h = await Harness.StartAsync(login: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.PostAsync("/mcp", Json())).StatusCode);
    }

    [Fact]
    public async Task A_login_not_on_the_list_gets_403()
    {
        await using var h = await Harness.StartAsync(login: "stranger@example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.PostAsync("/mcp", Json())).StatusCode);
    }

    [Fact]
    public async Task A_browser_origin_gets_403()
    {
        await using var h = await Harness.StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json() };
        req.Headers.Add("Origin", "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task A_non_json_post_gets_415()
    {
        await using var h = await Harness.StartAsync();
        var resp = await h.Http.PostAsync("/mcp", new StringContent("x", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    [Fact]
    public async Task Health_is_anonymous_and_says_nothing_sensitive()
    {
        await using var h = await Harness.StartAsync(login: null);
        var resp = await h.Http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.DoesNotContain(Harness.ApiKey, await resp.Content.ReadAsStringAsync());
    }
}

public class HostTests
{
    [Fact]
    public void Startup_summary_reports_not_configured_without_values()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var text = Harness.Summary(Harness.NewSettings(dir, key: false, pin: false), 49);
        Assert.Contains("not configured", text);
        Assert.Contains("writes off", text);
        Assert.Contains("49 tools", text);
    }

    [Fact]
    public void Startup_summary_never_contains_the_key_or_pin()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var text = Harness.Summary(Harness.NewSettings(dir), 49);
        Assert.Contains("configured", text);
        Assert.DoesNotContain(Harness.ApiKey, text);
        Assert.DoesNotContain(Harness.Pin, text);
    }

    [Fact]
    public async Task The_real_server_answers_on_a_unix_socket_with_nothing_configured()
    {
        var dir = Directory.CreateTempSubdirectory("umcp").FullName;
        var sock = Path.Combine(dir, "m.sock");
        var settings = Harness.NewSettings(dir, key: false, pin: false) with { Listen = "unix:" + sock };
        await using var app = McpHost.Build(settings);
        await app.StartAsync();
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(sock), ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        }) { BaseAddress = new Uri("http://localhost") };
        Assert.Equal("ok", await http.GetStringAsync("/healthz"));
        await app.StopAsync();
    }
}
