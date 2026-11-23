using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace UnifiMcp;

/// <summary>
/// The setup page's key and the browser sessions it opened. The key is random per start, so a page left open from an
/// earlier run asks for the new link. A browser that presents it gets a session value of its own in its cookie, never the
/// key: a cookie sent somewhere it shouldn't go (another service on localhost, which shares cookies across ports) is
/// not the link. At most <see cref="MaxSessions"/> sessions are kept; the oldest goes first.
/// </summary>
public sealed class SetupKey
{
    public const int MaxSessions = 20;

    private readonly Lock _lock = new();
    private readonly Queue<string> _sessions = new();

    public string Value { get; } = Random();

    public bool Matches(string? presented) => Same(presented, Value);

    /// <summary>A new session for a browser that presented the key.</summary>
    public string Open()
    {
        var session = Random();
        lock (_lock)
        {
            _sessions.Enqueue(session);
            while (_sessions.Count > MaxSessions) _sessions.Dequeue();
        }
        return session;
    }

    public bool IsSession(string? presented)
    {
        if (presented is not { Length: > 0 }) return false;
        lock (_lock) return _sessions.Any(s => Same(presented, s));
    }

    private static bool Same(string? a, string b) =>
        a is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Random() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The setup page (MCP_SETUP_PAGE): the live sign-in code, live checks of every setting, and the README's setup guides
/// with this server's values. It answers only on its own listener (MCP_SETUP_LISTEN, 127.0.0.1 by default), which the
/// tunnel never reaches, and only to a browser that opened the link from the server's log: the link carries a key, traded
/// at once for a same-site cookie scoped to /setup. Whoever can read the log could read the code there anyway, so the
/// page gives nobody more than the log does. Its answers name settings and show fingerprints, never a secret's value.
/// </summary>
public static class SetupPage
{
    public const string Path = "/setup";
    public const string CookieName = "unifi-setup";
    private const string LogCategory = "UnifiMcp.Setup";

    public static void Add(WebApplicationBuilder b, Settings s)
    {
        if (!s.SetupPage) return;
        b.Services.AddSingleton<SetupKey>();
        b.Services.AddSingleton(sp => new SetupGuide(sp.GetRequiredService<Settings>()));
        b.Services.TryAddSingleton<ICertificateProbe, TlsCertificateProbe>();
        b.Services.TryAddKeyedSingleton<HttpMessageHandler>(SetupChecks.OutboundKey, (_, _) => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        b.Services.AddSingleton<SetupChecks>();
    }

    /// <summary>The link the startup log prints: a wildcard address becomes localhost, the one a browser on the host can open.</summary>
    public static string Link(Settings s, SetupKey key)
    {
        var (address, port) = Listeners.SetupEndpoint(s.SetupListen)!.Value;
        var host = address is null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? "localhost"
            : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"http://{host}:{port}{Path}?key={key.Value}";
    }

    public static void Map(WebApplication app)
    {
        var settings = app.Services.GetRequiredService<Settings>();
        if (!settings.SetupPage) return;
        var setup = app.MapGroup(Path).AllowAnonymous().ExcludeFromDescription()
            // RouteTable gives /setup to the setup listener alone, so PublicGate keeps the others off it; this holds even if
            // that ever changes.
            .AddEndpointFilter(async (ctx, next) => ctx.HttpContext.ListenerOf() == Listener.Setup ? await next(ctx) : Results.NotFound());

        setup.MapGet("", Page); // also matches /setup/
        setup.MapGet("/setup.css", () => Asset("setup/setup.css", "text/css; charset=utf-8"));
        setup.MapGet("/setup.js", () => Asset("setup/setup.js", "text/javascript; charset=utf-8"));
        setup.MapGet("/icon.png", (HttpContext http) =>
        {
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(Convert.FromBase64String(IconData.Icon32), "image/png");
        });
        setup.MapGet("/images/{name}", (string name, HttpContext http) =>
        {
            if (SetupGuide.Image(name) is not { } svg) return Results.NotFound();
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
            return Results.Bytes(svg, "image/svg+xml");
        });

        setup.MapPost("/api/check/{id}", async Task<IResult> (string id, HttpContext http, SetupKey key, SetupChecks checks, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (!Unlocked(http, key)) return Locked();
            if (!SetupChecks.Ids.Contains(id)) return Results.NotFound();
            NoStore(http);
            try
            {
                return Results.Json(await checks.RunAsync(id, ct));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // A check that breaks in a way it didn't foresee still answers as a failed check, so the page can say so.
                lf.CreateLogger(LogCategory).LogWarning(e, "The {Check} check failed unexpectedly", id);
                return Results.Json(new CheckResult(id, CheckStatus.Fail, $"The check itself failed: {e.Message}",
                    "The server's log has the details."));
            }
        });

        setup.MapPost("/api/code", async Task<IResult> (HttpContext http, SetupKey key, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (!Unlocked(http, key)) return Locked();
            NoStore(http);
            if (http.RequestServices.GetService<SignInCodes>() is not { } codes)
                return Results.Json(new { error = "Sign-in codes are off: the connector isn't configured, or MCP_SIGNIN isn't code." }, statusCode: 404);
            var body = await http.Request.ReadFromJsonAsync<CodeRequest>(ct);
            if (body?.New != true)
                return Results.Json(codes.Peek() is { } live ? new CodeAnswer(live.Code, live.ExpiresAt) : new CodeAnswer(null, null));
            if (codes.Current() is not { } active)
                return Results.Json(new { error = $"{SignInCodes.MaxCodesPerHour} codes were issued in the last hour. Try again later." }, statusCode: 429);
            lf.CreateLogger(LogCategory).LogInformation("Sign-in code issued on the setup page, valid until {Expires} UTC",
                active.ExpiresAt.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture));
            return Results.Json(new CodeAnswer(active.Code, active.ExpiresAt));
        });
    }

    private sealed record CodeRequest(bool New);
    private sealed record CodeAnswer(string? Code, DateTimeOffset? ExpiresAt);

    private static async Task Page(HttpContext http, SetupKey key, Settings settings, SetupGuide guide, ConnectorState connector)
    {
        var presented = http.Request.Query["key"].ToString();
        if (presented.Length > 0)
        {
            if (!key.Matches(presented))
            {
                await Write(http, StatusCodes.Status403Forbidden, SetupHtml.Locked(settings, "That link's key isn't this server's. The server makes a new one each time it starts: open the link from its latest startup."));
                return;
            }
            // Trade the key in the address bar for a cookie, and drop it from the URL (and the browser's history).
            http.Response.Cookies.Append(CookieName, key.Open(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = Path,
                IsEssential = true,
            });
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = Path;
            NoStore(http);
            return;
        }
        if (!Unlocked(http, key))
        {
            var stale = http.Request.Cookies.ContainsKey(CookieName);
            await Write(http, StatusCodes.Status401Unauthorized, SetupHtml.Locked(settings,
                stale ? "The server has restarted since this page was opened, so its key changed." : null));
            return;
        }
        var codes = http.RequestServices.GetService<SignInCodes>() is not null;
        await Write(http, StatusCodes.Status200OK, SetupHtml.Page(settings, guide, connector, codes));
    }

    private static bool Unlocked(HttpContext http, SetupKey key) => key.IsSession(http.Request.Cookies[CookieName]);

    /// <summary>
    /// A host a browser on the machine uses: localhost or an IP address. A DNS name is refused, which stops DNS rebinding
    /// and a tunnel route pointed here by mistake (its requests carry the public host name).
    /// </summary>
    public static bool HostAllowed(HostString host) =>
        host.HasValue
        && (string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(host.Host.Trim('[', ']'), out _));

    private static IResult Locked() =>
        Results.Json(new { error = "This page is locked: open the setup link from the server's log." }, statusCode: StatusCodes.Status401Unauthorized);

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static IResult Asset(string name, string type) =>
        SetupGuide.ReadResource(name) is { } text ? Results.Text(text, type) : Results.NotFound();

    private static Task Write(HttpContext http, int status, string html)
    {
        var r = http.Response;
        r.StatusCode = status;
        r.ContentType = "text/html; charset=utf-8";
        NoStore(http);
        r.Headers.XFrameOptions = "DENY";
        r.Headers.XContentTypeOptions = "nosniff";
        r.Headers["Referrer-Policy"] = "same-origin";
        r.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        return r.WriteAsync(html);
    }
}
