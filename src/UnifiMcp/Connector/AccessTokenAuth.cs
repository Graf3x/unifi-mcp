using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UnifiMcp;

/// <summary>
/// MCP_ACCESS_TOKEN: a fixed credential for the public <c>/mcp</c>, for clients that send a static header instead of doing
/// OAuth (Claude's "Request headers", Claude Code's <c>--header</c>). Accepted as <c>Authorization: Bearer &lt;token&gt;</c> or
/// <c>X-Api-Key: &lt;token&gt;</c>, compared in constant time. It never opens the owner's pages and is never accepted on the
/// tailnet socket; with none configured, nothing here matches. On the LAN listener (MCP_LAN_LISTEN) the same handler takes
/// MCP_LAN_ACCESS_TOKEN instead, and only that: the public token is never accepted there, nor the LAN token on the public side.
/// </summary>
public sealed class AccessTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Settings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AccessToken";
    public const string Subject = "access-token";
    public const string LanSubject = "lan-access-token";
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>The token this request's listener accepts: the LAN token on the LAN listener, else MCP_ACCESS_TOKEN.</summary>
    public static string? TokenFor(HttpContext ctx, Settings settings) =>
        ctx.ListenerOf() == Listener.Lan ? settings.LanAccessToken : settings.AccessToken;

    /// <summary>Whether the request carries the given token in either header.</summary>
    public static bool Presented(HttpRequest request, string? expected)
    {
        if (expected is not { } token) return false;
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Same(auth[7..].Trim(), token)) return true;
        return request.Headers[ApiKeyHeader].ToString() is { Length: > 0 } key && Same(key.Trim(), token);
    }

    /// <summary>Hashing first makes the comparison constant-time whatever the lengths.</summary>
    private static bool Same(string presented, string token) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Presented(Request, TokenFor(Context, settings))) return Task.FromResult(AuthenticateResult.NoResult());
        var lan = Context.ListenerOf() == Listener.Lan;
        var identity = new ClaimsIdentity([new Claim("sub", lan ? LanSubject : Subject), new Claim("name", lan ? "LAN access token" : "access token")],
            SchemeName, "name", null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
