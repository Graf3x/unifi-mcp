namespace UnifiMcp;

/// <summary>
/// Keeps the listeners apart. Runs before authentication: an unknown listener is refused outright, a public
/// request never carries the Tailscale header on to the tailnet handler, and each side only sees its own routes.
/// </summary>
public sealed class PublicGate(RequestDelegate next, Settings settings, ConnectorState state)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        switch (ctx.ListenerOf())
        {
            case Listener.Unknown:
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;

            case Listener.Public:
                // With no PublicBaseUrl there is nothing to compare against; the 503 below answers instead.
                if (settings.PublicBaseUrl is { } baseUrl)
                {
                    if (!HostMatches(ctx.Request.Host, baseUrl))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                        return;
                    }
                    // The match forgives case and an explicit default port; the URLs this side builds from the host (the
                    // SDK's resource_metadata challenge, the metadata documents) must not echo either.
                    ctx.Request.Host = baseUrl.IsDefaultPort ? new HostString(baseUrl.Host) : new HostString(baseUrl.Host, baseUrl.Port);
                }
                ctx.Request.Headers.Remove(CallerPolicy.LoginHeader);
                // cloudflared terminates TLS and reaches the socket over plain http; every URL this side builds (the
                // resource_metadata challenge, redirects) must name the public https address.
                ctx.Request.Scheme = Uri.UriSchemeHttps;
                if (!state.Ready)
                {
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync("The connector isn't set up yet.");
                    return;
                }
                if (!RouteTable.Serves(Listener.Public, ctx.Request.Path))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                break;

            case Listener.Setup:
                // The setup listener serves the setup page and nothing else: no MCP, no OAuth, no sign-in pages.
                if (!RouteTable.Serves(Listener.Setup, ctx.Request.Path))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (!SetupPage.HostAllowed(ctx.Request.Host))
                {
                    ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync("Open the setup page as localhost or by IP address, from the link in the server's log.");
                    return;
                }
                ctx.Request.Headers.Remove(CallerPolicy.LoginHeader);
                break;

            case Listener.Lan:
                // Token-only: the MCP endpoint and the health check, nothing else. The Tailscale header means nothing here.
                if (!RouteTable.Serves(Listener.Lan, ctx.Request.Path))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                ctx.Request.Headers.Remove(CallerPolicy.LoginHeader);
                break;

            case Listener.Tailnet:
                if (OnlyPublic(ctx.Request.Path) || ctx.Request.Path.StartsWithSegments(SetupPage.Path, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                break;
        }
        await next(ctx);
    }

    private static bool HostMatches(HostString host, Uri baseUrl) =>
        string.Equals(host.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase)
        && (host.Port is null || host.Port == baseUrl.Port);

    private static bool Starts(PathString p, string prefix) =>
        p.Value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;

    private static bool OnlyPublic(PathString p) =>
        p.StartsWithSegments("/oauth", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/signin-google", StringComparison.OrdinalIgnoreCase)
        || p.Equals(CodeSignIn.Path, StringComparison.OrdinalIgnoreCase)
        || Starts(p, "/connections")
        // Segment matching also covers the RFC 8414 path-suffixed forms (…/oauth-authorization-server/mcp).
        || p.StartsWithSegments("/.well-known/oauth-authorization-server", StringComparison.OrdinalIgnoreCase)
        || p.StartsWithSegments("/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase);
}
