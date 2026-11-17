using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;

namespace UnifiMcp;

/// <summary>
/// The one-time codes of MCP_SIGNIN=code. At most one code is live: ten digits, valid for <see cref="Lifetime"/>, good for
/// one sign-in, and thrown away after <see cref="MaxAttempts"/> wrong guesses. At most <see cref="MaxCodesPerHour"/> new
/// codes per hour, so guessing stays hopeless however many addresses an attacker uses. In memory only: a restart forgets it.
/// </summary>
public sealed class SignInCodes(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 5;
    public const int MaxCodesPerHour = 10;

    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _issued = new();
    private string? _code;
    private DateTimeOffset _expiresAt;
    private int _failures;

    public sealed record Active(string Code, DateTimeOffset ExpiresAt);

    /// <summary>The live code, or a new one; null when <see cref="MaxCodesPerHour"/> were issued in the last hour.</summary>
    public Active? Current()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (_code is not null && now < _expiresAt) return new(_code, _expiresAt);
            _code = null;
            while (_issued.Count > 0 && now - _issued.Peek() >= TimeSpan.FromHours(1)) _issued.Dequeue();
            if (_issued.Count >= MaxCodesPerHour) return null;
            _code = string.Create(CultureInfo.InvariantCulture, $"{RandomNumberGenerator.GetInt32(100_000):D5}-{RandomNumberGenerator.GetInt32(100_000):D5}");
            _expiresAt = now + Lifetime;
            _failures = 0;
            _issued.Enqueue(now);
            return new(_code, _expiresAt);
        }
    }

    /// <summary>True once for the live code (spaces and the dash are ignored); a wrong guess counts toward <see cref="MaxAttempts"/>.</summary>
    public bool TryRedeem(string? presented)
    {
        var digits = Encoding.ASCII.GetBytes(new string((presented ?? "").Where(char.IsAsciiDigit).ToArray()));
        lock (_lock)
        {
            if (_code is null || time.GetUtcNow() >= _expiresAt)
            {
                _code = null;
                return false;
            }
            if (CryptographicOperations.FixedTimeEquals(digits, Encoding.ASCII.GetBytes(_code.Replace("-", "", StringComparison.Ordinal))))
            {
                _code = null;
                return true;
            }
            if (++_failures >= MaxAttempts) _code = null;
            return false;
        }
    }
}

/// <summary>
/// The owner's sign-in page in code mode: GET writes the live code to the server log (never to the page), POST takes it
/// and issues the same <c>unifi.auth</c> cookie Google sign-in does, for a fixed owner identity.
/// </summary>
public static class CodeSignIn
{
    public const string Path = "/signin-code";
    /// <summary>The subject every code sign-in shares: there is one owner, whoever can read the log.</summary>
    public const string Subject = "owner";
    public const string Label = "the owner (signed in with a code)";
    public const string RateLimitPolicy = "signin-code";
    private const string MethodClaim = "signin";
    private const string LogCategory = "UnifiMcp.SignIn";

    public static bool IsCodeOwner(ClaimsPrincipal p) =>
        p.FindFirst("sub")?.Value == Subject && p.FindFirst(MethodClaim)?.Value == "code";

    public static void Map(WebApplication app)
    {
        app.MapGet(Path, async Task<IResult> (HttpContext http, SignInCodes codes, Settings settings, ILoggerFactory lf) =>
            {
                var returnUrl = LocalReturnUrl(http.Request.Query["returnUrl"].ToString());
                var log = lf.CreateLogger(LogCategory);
                if (codes.Current() is not { } active)
                {
                    log.LogWarning("No new sign-in code: {Max} were issued in the last hour (from {Ip})", SignInCodes.MaxCodesPerHour, OAuthEndpoints.ClientIp(http));
                    await Pages.WriteAsync(http.Response, StatusCodes.Status429TooManyRequests,
                        Pages.ErrorPage("Too many sign-in codes", "Too many sign-in codes were asked for in the last hour. Try again later."));
                    return Results.Empty;
                }
                // The one place the code appears: whoever can read this log is the owner.
                log.LogWarning("Sign-in code {Code} for {Url}: one use, valid until {Expires} UTC (asked from {Ip})", active.Code,
                    new Uri(settings.PublicBaseUrl!, Path), active.ExpiresAt.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture), OAuthEndpoints.ClientIp(http));
                await Pages.WriteAsync(http.Response, StatusCodes.Status200OK, FormPage(returnUrl, null));
                return Results.Empty;
            })
            .RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();

        app.MapPost(Path, async Task<IResult> (HttpContext http, SignInCodes codes, ILoggerFactory lf, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.StatusCode(StatusCodes.Status400BadRequest);
                var form = await http.Request.ReadFormAsync(ct);
                var returnUrl = LocalReturnUrl(form["returnUrl"].ToString());
                var log = lf.CreateLogger(LogCategory);
                if (!codes.TryRedeem(form["code"].ToString()))
                {
                    log.LogWarning("Code sign-in refused from {Ip}", OAuthEndpoints.ClientIp(http));
                    await Pages.WriteAsync(http.Response, StatusCodes.Status400BadRequest, FormPage(returnUrl,
                        "That code isn't right, or it was used or has expired. Reload this page and use the code the server log shows now."));
                    return Results.Empty;
                }

                var identity = new ClaimsIdentity([new Claim("sub", Subject), new Claim("name", Label), new Claim(MethodClaim, "code")],
                    GoogleSignIn.CookieScheme, "name", null);
                await http.SignInAsync(GoogleSignIn.CookieScheme, new ClaimsPrincipal(identity));
                log.LogInformation("Code sign-in from {Ip}", OAuthEndpoints.ClientIp(http));
                http.Response.StatusCode = StatusCodes.Status303SeeOther;
                http.Response.Headers.Location = returnUrl;
                http.Response.Headers.CacheControl = "no-store";
                return Results.Empty;
            })
            .RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
    }

    /// <summary>A path on this server, or /connections: never another host (no scheme, no "//" or "/\" start, no control characters).</summary>
    internal static string LocalReturnUrl(string? url) =>
        url is { Length: > 0 } u && u[0] == '/' && (u.Length == 1 || (u[1] != '/' && u[1] != '\\')) && !u.Any(char.IsControl)
            ? u : "/connections";

    private static string FormPage(string returnUrl, string? error)
    {
        var problem = error is null ? "" : $"<p><b>{Pages.Encode(error)}</b></p>";
        var body = $"""
            <h1>Sign in to UniFi Network</h1>
            {problem}
            <p>Enter the sign-in code from the server's log. It looks like <code>01234-56789</code>, works once, and expires after
            {(int)SignInCodes.Lifetime.TotalMinutes} minutes. With Docker Compose: <code>docker compose logs unifi-mcp</code>.</p>
            <form method="post" action="{Path}">
              <input type="hidden" name="returnUrl" value="{Pages.Encode(returnUrl)}">
              <input name="code" inputmode="numeric" autocomplete="one-time-code" required autofocus>
              <button type="submit">Sign in</button>
            </form>
            """;
        return Pages.Html("Sign in", body);
    }
}
