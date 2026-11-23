namespace UnifiMcp;

/// <summary>
/// One route of <see cref="RouteTable"/>: a path, the listeners that serve it, and how the public side treats it.
/// </summary>
/// <param name="WholeSegment">The path and everything beneath it (<c>/connections/…</c>); otherwise the path alone.</param>
/// <param name="Browser">A public page a browser talks to: it accepts the connector's own origin and form posts.</param>
/// <param name="SendsToSignIn">A page visit (GET or HEAD) without the owner's cookie is sent to sign-in.</param>
public sealed record Route(string Path, bool WholeSegment, IReadOnlyList<Listener> On, bool Browser = false, bool SendsToSignIn = false)
{
    /// <summary>Case-insensitive; a trailing slash is forgiven, as routing forgives it.</summary>
    public bool Matches(PathString p) =>
        WholeSegment
            ? p.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase)
            : p.Equals(Path, StringComparison.OrdinalIgnoreCase) || p.Equals(Path + "/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every path the server can serve, and which listeners serve it: what <see cref="PublicGate"/> lets through on each
/// listener (anything not here is a 404 before authentication), which public paths are browser pages for
/// <see cref="RequestGuard"/>, and which send a visit to sign-in. A route that exists only in some setups (code
/// sign-in, the setup page) is listed regardless; where it is not mapped, routing answers 404.
/// </summary>
public static class RouteTable
{
    private static readonly Listener[] Everywhere = [Listener.Public, Listener.Tailnet, Listener.Lan];
    private static readonly Listener[] PublicAndTailnet = [Listener.Public, Listener.Tailnet];
    private static readonly Listener[] PublicOnly = [Listener.Public];

    public static IReadOnlyList<Route> Entries { get; } =
    [
        new("/mcp", false, Everywhere),
        // The health check is a browser page on the public side as it always was: nothing there needs it not to be.
        new("/healthz", false, Everywhere, Browser: true),
        .. ConnectionsPage.IconPaths.Select(p => new Route(p, false, PublicAndTailnet)),

        // Segments also cover the path-suffixed forms (RFC 8414 and RFC 9728, …/mcp). The resource metadata is the MCP
        // SDK's, answered inside its authentication handler.
        new("/.well-known/oauth-protected-resource", true, PublicOnly, Browser: true),
        new("/.well-known/oauth-authorization-server", true, PublicOnly, Browser: true),
        new("/.well-known/openid-configuration", true, PublicOnly, Browser: true),
        new("/oauth/jwks", false, PublicOnly, Browser: true),
        new("/oauth/register", false, PublicOnly, Browser: true),
        new("/oauth/authorize", false, PublicOnly, Browser: true, SendsToSignIn: true),
        new("/oauth/consent", false, PublicOnly, Browser: true),
        new("/oauth/token", false, PublicOnly, Browser: true),
        new("/oauth/revoke", false, PublicOnly, Browser: true),
        // Answered inside the Google OIDC handler.
        new(GoogleSignIn.CallbackPath, false, PublicOnly, Browser: true),
        new(CodeSignIn.Path, false, PublicOnly, Browser: true),
        new("/connections", true, PublicOnly, Browser: true, SendsToSignIn: true),

        new(SetupPage.Path, true, [Listener.Setup]),
    ];

    /// <summary>Whether <paramref name="listener"/> serves <paramref name="path"/>. Unknown serves nothing.</summary>
    public static bool Serves(Listener listener, PathString path) =>
        Entries.Any(r => r.On.Contains(listener) && r.Matches(path));

    /// <summary>Whether <paramref name="path"/> is one of the public side's browser pages.</summary>
    public static bool IsBrowserPage(PathString path) =>
        Entries.Any(r => r.Browser && r.On.Contains(Listener.Public) && r.Matches(path));

    /// <summary>Whether a page visit to <paramref name="path"/> without the owner's cookie is sent to sign-in.</summary>
    public static bool SendsToSignIn(PathString path) =>
        Entries.Any(r => r.SendsToSignIn && r.Matches(path));
}
