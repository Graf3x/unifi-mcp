using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UnifiMcp;

public enum CallerDecision { Allow, DenyUntrustedTransport, DenyNoIdentity, DenyNotAllowed }

public enum CallerTransport { UnixSocket, LoopbackTcp, RemoteTcp }

/// <summary>
/// Who may call on the tailnet socket. In production the server listens only on a Unix socket; only
/// <c>tailscale serve</c> on the host connects to it, and it stamps the tailnet caller's login in
/// <c>Tailscale-User-Login</c>. TCP is refused (loopback can be enabled for local development) because anyone who
/// can reach a TCP port could forge that header.
/// </summary>
public static class CallerPolicy
{
    public const string LoginHeader = "Tailscale-User-Login";

    public static CallerTransport TransportOf(IPAddress? remote) =>
        remote is null ? CallerTransport.UnixSocket
        : IPAddress.IsLoopback(remote) ? CallerTransport.LoopbackTcp
        : CallerTransport.RemoteTcp;

    public static CallerDecision Evaluate(CallerTransport transport, string? login, IReadOnlyCollection<string> allowed, bool allowLoopback)
    {
        var trusted = transport == CallerTransport.UnixSocket || (transport == CallerTransport.LoopbackTcp && allowLoopback);
        if (!trusted) return CallerDecision.DenyUntrustedTransport;
        if (string.IsNullOrWhiteSpace(login)) return CallerDecision.DenyNoIdentity;
        return allowed.Any(a => string.Equals(a.Trim(), login.Trim(), StringComparison.OrdinalIgnoreCase))
            ? CallerDecision.Allow
            : CallerDecision.DenyNotAllowed;
    }
}

/// <summary>
/// Refuses what only a browser sends: any Origin header, and POSTs that are not JSON. MCP clients send neither.
/// The public listener's OAuth and sign-in pages are the exception: a browser talks to them, so they accept the
/// connector's own origin and form posts, and the setup page its own origin.
/// </summary>
public static class RequestGuard
{
    /// <param name="selfOrigin">The request's own origin: the setup page's scripts call back to it.</param>
    public static int? Check(Listener listener, string path, string method, string? origin, string? contentType, string? publicOrigin,
        string? selfOrigin = null)
    {
        var media = contentType?.Split(';')[0].Trim();
        if (listener == Listener.Setup)
        {
            if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, selfOrigin, StringComparison.OrdinalIgnoreCase))
                return StatusCodes.Status403Forbidden;
            if (HttpMethods.IsPost(method) && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
                return StatusCodes.Status415UnsupportedMediaType;
            return null;
        }
        var mcp = new PathString(path);
        var browserPage = listener == Listener.Public
            && !mcp.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
            && !mcp.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
        if (!browserPage)
        {
            if (!string.IsNullOrEmpty(origin)) return StatusCodes.Status403Forbidden;
            if (HttpMethods.IsPost(method) && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
                return StatusCodes.Status415UnsupportedMediaType;
            return null;
        }

        if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, publicOrigin, StringComparison.OrdinalIgnoreCase))
            return StatusCodes.Status403Forbidden;
        if (HttpMethods.IsPost(method)
            && !string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(media, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return StatusCodes.Status415UnsupportedMediaType;
        return null;
    }
}

/// <summary>
/// The bearer scheme of a connector that is not configured (no JWT scheme is registered then): never authenticates,
/// challenges 401. The public listener answers 503 before authentication in that state anyway.
/// </summary>
public sealed class PlaceholderBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>Turns <see cref="CallerPolicy"/> into ASP.NET authentication. Every refusal is a bare 403.</summary>
public sealed class TailscaleServeAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Settings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TailscaleServe";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var login = Request.Headers[CallerPolicy.LoginHeader].ToString();
        var transport = CallerPolicy.TransportOf(Context.Connection.RemoteIpAddress);
        var decision = CallerPolicy.Evaluate(transport, login, settings.AllowedLogins(), settings.AllowLoopbackTcp);
        if (decision != CallerDecision.Allow)
        {
            Logger.LogWarning("Refused caller {Remote} ({Decision})", Context.Connection.RemoteIpAddress, decision);
            return Task.FromResult(AuthenticateResult.Fail(decision.ToString()));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, login.Trim())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
