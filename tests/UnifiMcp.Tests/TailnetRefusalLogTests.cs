using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace UnifiMcp.Tests;

/// <summary>
/// The tailnet listener logs a refused caller only when an endpoint needed one. The container health check GETs
/// /healthz on mcp.sock with no login every 30 seconds, and that is not a refusal (issue #4).
/// </summary>
public sealed class TailnetRefusalLogTests
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    private static async Task<(HttpStatusCode Status, IReadOnlyList<string> Warnings)> SendAsync(Func<HttpRequestMessage> request)
    {
        var logs = new LogCapture();
        var dir = Directory.CreateTempSubdirectory("unifi-mcp-refusal").FullName;
        try
        {
            await using var app = McpHost.Build(Harness.NewSettings(dir), new FakeUnifi(),
                b => { b.WebHost.UseTestServer(); b.Logging.AddProvider(logs); }, _ => Listener.Tailnet);
            await app.StartAsync();
            var status = (await app.GetTestClient().SendAsync(request())).StatusCode;
            await app.StopAsync();
            return (status, [.. logs.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Line)]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static HttpRequestMessage Mcp(string? login)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        r.Headers.Accept.ParseAdd("application/json");
        r.Headers.Accept.ParseAdd("text/event-stream");
        if (login is not null) r.Headers.Add(CallerPolicy.LoginHeader, login);
        return r;
    }

    [Fact]
    public async Task The_health_check_without_a_login_logs_no_warning()
    {
        var (status, warnings) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "/healthz"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(warnings, w => w.Contains("Refused caller"));
    }

    [Fact]
    public async Task Mcp_without_a_login_is_still_refused_with_a_warning()
    {
        var (status, warnings) = await SendAsync(() => Mcp(login: null));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(warnings, w => w.Contains("Refused caller") && w.Contains(nameof(CallerDecision.DenyNoIdentity)));
    }

    [Fact]
    public async Task Mcp_with_a_login_off_the_list_is_still_refused_with_a_warning()
    {
        var (status, warnings) = await SendAsync(() => Mcp(login: "stranger@example.test"));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(warnings, w => w.Contains("Refused caller") && w.Contains(nameof(CallerDecision.DenyNotAllowed)));
    }

    [Fact]
    public async Task Mcp_with_an_allowed_login_logs_no_refusal()
    {
        var (status, warnings) = await SendAsync(() => Mcp(Harness.Login));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(warnings, w => w.Contains("Refused caller"));
    }
}
