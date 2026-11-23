using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace UnifiMcp.Tests;

public class RouteTableTests
{
    [Theory]
    [InlineData(Listener.Public, "/mcp", true)]
    [InlineData(Listener.Public, "/MCP", true)]
    [InlineData(Listener.Public, "/mcp/", true)]
    [InlineData(Listener.Public, "/healthz/", true)]
    [InlineData(Listener.Public, "/mcp/icon-512.png", true)]
    [InlineData(Listener.Public, "/mcp/icon-evil.png", false)]
    [InlineData(Listener.Public, "/.well-known/oauth-protected-resource", true)]
    [InlineData(Listener.Public, "/.well-known/oauth-protected-resource/mcp", true)]
    [InlineData(Listener.Public, "/.well-known/oauth-authorization-server/mcp", true)]
    [InlineData(Listener.Public, "/.well-known/openid-configuration", true)]
    [InlineData(Listener.Public, "/.well-known/security.txt", false)]
    [InlineData(Listener.Public, "/oauth/token", true)]
    [InlineData(Listener.Public, "/oauth/authorize/", true)]
    [InlineData(Listener.Public, "/oauth/other", false)]
    [InlineData(Listener.Public, GoogleSignIn.CallbackPath, true)]
    [InlineData(Listener.Public, CodeSignIn.Path, true)]
    [InlineData(Listener.Public, "/connections", true)]
    [InlineData(Listener.Public, "/connections/abc/disconnect", true)]
    [InlineData(Listener.Public, "/connectionsX", false)]
    [InlineData(Listener.Public, "/signout-oidc", false)]
    [InlineData(Listener.Public, SetupPage.Path, false)]
    [InlineData(Listener.Public, "/", false)]
    [InlineData(Listener.Tailnet, "/mcp", true)]
    [InlineData(Listener.Tailnet, "/mcp/", true)]
    [InlineData(Listener.Tailnet, "/healthz", true)]
    [InlineData(Listener.Tailnet, "/mcp/icon-32.png", true)]
    [InlineData(Listener.Tailnet, "/.well-known/oauth-protected-resource", false)]
    [InlineData(Listener.Tailnet, "/.well-known/oauth-authorization-server", false)]
    [InlineData(Listener.Tailnet, "/oauth/token", false)]
    [InlineData(Listener.Tailnet, "/connections", false)]
    [InlineData(Listener.Tailnet, "/connectionsX", false)]
    [InlineData(Listener.Tailnet, "/signout-callback-oidc", false)]
    [InlineData(Listener.Tailnet, SetupPage.Path, false)]
    [InlineData(Listener.Lan, "/mcp", true)]
    [InlineData(Listener.Lan, "/healthz/", true)]
    [InlineData(Listener.Lan, "/mcp/icon-512.png", false)]
    [InlineData(Listener.Lan, "/oauth/token", false)]
    [InlineData(Listener.Setup, "/setup", true)]
    [InlineData(Listener.Setup, "/setup/", true)]
    [InlineData(Listener.Setup, "/setup/setup.js", true)]
    [InlineData(Listener.Setup, "/setupx", false)]
    [InlineData(Listener.Setup, "/mcp", false)]
    [InlineData(Listener.Setup, "/healthz", false)]
    [InlineData(Listener.Unknown, "/healthz", false)]
    public void Each_listener_serves_only_its_own_paths(Listener listener, string path, bool served) =>
        Assert.Equal(served, RouteTable.Serves(listener, new PathString(path)));

    [Theory]
    [InlineData("/mcp", false)]
    [InlineData("/mcp/", false)]
    [InlineData("/mcp/icon-32.png", false)]
    [InlineData("/healthz", true)]
    [InlineData("/.well-known/oauth-authorization-server", true)]
    [InlineData("/oauth/token", true)]
    [InlineData(GoogleSignIn.CallbackPath, true)]
    [InlineData(CodeSignIn.Path, true)]
    [InlineData("/connections/abc/disconnect", true)]
    public void The_public_side_s_browser_pages_are_all_but_mcp_and_its_icons(string path, bool browser) =>
        Assert.Equal(browser, RouteTable.IsBrowserPage(new PathString(path)));

    [Theory]
    [InlineData("/oauth/authorize", true)]
    [InlineData("/OAUTH/authorize/", true)]
    [InlineData("/connections", true)]
    [InlineData("/connections/abc", true)]
    [InlineData("/oauth/token", false)]
    [InlineData("/oauth/consent", false)]
    [InlineData(GoogleSignIn.CallbackPath, false)]
    [InlineData("/mcp", false)]
    public void Only_the_owner_s_pages_send_a_visit_to_sign_in(string path, bool sends) =>
        Assert.Equal(sends, RouteTable.SendsToSignIn(new PathString(path)));

    /// <summary>Answered by an authentication handler, not by a mapped endpoint, so no route pattern names them.</summary>
    private static readonly string[] AnsweredByHandlers =
    [
        GoogleSignIn.CallbackPath, // the Google OIDC handler's callback
        "/.well-known/oauth-protected-resource", // the MCP SDK's resource metadata, at the root and under /mcp
    ];

    /// <summary>
    /// The table against the real host, with everything mapped (connector, code sign-in, setup page): no route goes
    /// unlisted, and no entry outlives its route.
    /// </summary>
    [Fact]
    public async Task The_table_and_the_mapped_routes_agree()
    {
        await using var h = await PublicHarness.StartAsync(signIn: SignInMode.Code, settings: s => s with { SetupPage = true });
        var mapped = ((IEndpointRouteBuilder)h.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Select(e => Sample(e.RoutePattern.RawText ?? ""))
            .Distinct().ToList();
        Assert.Contains("/connections/x/disconnect", mapped);

        var unlisted = mapped.Where(p => !RouteTable.Entries.Any(r => r.Matches(new PathString(p)))).ToList();
        Assert.True(unlisted.Count == 0, "Mapped but not in the route table: " + string.Join(", ", unlisted));

        var stale = RouteTable.Entries
            .Where(r => !AnsweredByHandlers.Contains(r.Path) && !mapped.Any(p => r.Matches(new PathString(p))))
            .Select(r => r.Path).ToList();
        Assert.True(stale.Count == 0, "In the route table but not mapped: " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_handler_answered_path_is_an_entry() =>
        Assert.All(AnsweredByHandlers, p => Assert.Contains(RouteTable.Entries, r => r.Path == p));

    /// <summary>A route pattern as a path a request could have: each parameter becomes "x".</summary>
    private static string Sample(string pattern)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{[^}]+\}", "x");
        return path.StartsWith('/') ? path : "/" + path;
    }
}
