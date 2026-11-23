using System.Globalization;
using System.Text;

namespace UnifiMcp;

/// <summary>
/// The owner's <c>/connections</c> page (the applications that hold a consent, with a Disconnect button each) and the
/// connector icons Claude shows. The page is cookie-only (the <c>google-owner</c> policy, whose validation re-checks the
/// allow-list on every use) and lists only the signed-in user's own consents; the cookie is only read here, never issued.
/// </summary>
public static class ConnectionsPage
{
    internal const int MaxNameLength = 100;
    private const string LogCategory = "UnifiMcp.Connections";

    private static readonly (int Size, byte[] Png)[] Icons =
    [
        (512, Convert.FromBase64String(IconData.Icon512)),
        (180, Convert.FromBase64String(IconData.Icon180)),
        (32, Convert.FromBase64String(IconData.Icon32)),
    ];

    /// <summary>Where the icons are served, e.g. <c>/mcp/icon-512.png</c>.</summary>
    internal static IEnumerable<string> IconPaths => Icons.Select(i => IconPath(i.Size));

    private static string IconPath(int size) => $"/mcp/icon-{size}.png";

    public static void Map(WebApplication app)
    {
        // Anonymous, no rate limit (the policies are opt-in per endpoint), and the same on either listener: it is public artwork.
        foreach (var (size, png) in Icons)
            app.MapGet(IconPath(size), (HttpContext http) =>
                {
                    http.Response.Headers.CacheControl = "public, max-age=86400";
                    http.Response.Headers.XContentTypeOptions = "nosniff";
                    return Results.Bytes(png, "image/png");
                })
                .AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/connections", async Task<IResult> (HttpContext http, GrantLedger ledger, CancellationToken ct) =>
            {
                if (OAuthUser.From(http.User) is not { } user) return Results.Unauthorized();
                var consents = await ledger.ListConsentsAsync(user, ct);
                await Pages.WriteAsync(http.Response, StatusCodes.Status200OK, Render(consents, user));
                return Results.Empty;
            })
            .RequireAuthorization(GoogleSignIn.Policy).ExcludeFromDescription();

        app.MapPost("/connections/{clientId}/disconnect", async Task<IResult> (string clientId, HttpContext http, GrantLedger ledger, Settings settings,
                ILoggerFactory lf, CancellationToken ct) =>
            {
                var log = lf.CreateLogger(LogCategory);
                // Stricter than the request guard (which lets a missing Origin through): a state change needs the page's own origin.
                var publicOrigin = settings.PublicBaseUrl?.GetLeftPart(UriPartial.Authority);
                var origin = http.Request.Headers.Origin.ToString();
                if (publicOrigin is null || !string.Equals(origin, publicOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning("Connections: disconnect refused, Origin '{Origin}' is not this server's", Short(LogText.Clean(origin)));
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                if (OAuthUser.From(http.User) is not { } user) return Results.Unauthorized();

                // Only the signed-in user's own consent: an unknown or someone else's client id is the same 404, and changes nothing.
                if (!await ledger.DisconnectAsync(user, clientId, ct))
                {
                    await Pages.WriteAsync(http.Response, StatusCodes.Status404NotFound,
                        Pages.ErrorPage("Not connected", "That application isn't connected."));
                    return Results.Empty;
                }
                log.LogInformation("Connections: {Email} disconnected client {ClientId}", LogText.Clean(user.Label), Short(LogText.Clean(clientId)));

                http.Response.StatusCode = StatusCodes.Status303SeeOther;
                http.Response.Headers.Location = "/connections";
                return Results.Empty;
            })
            .RequireAuthorization(GoogleSignIn.Policy).ExcludeFromDescription();
    }

    /// <summary>The page. Names and hosts come from clients (anything they registered or published): all of it is encoded and cut short.</summary>
    internal static string Render(IReadOnlyList<Consent> consents, OAuthUser user)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Connected applications</h1>");
        sb.Append($"<p>Signed in as {Pages.Encode(user.Label)}. These applications can use your UniFi Network tools until you disconnect them. "
            + "A disconnected application can no longer refresh; a token it already holds works until it expires, within the hour.</p>");
        if (consents.Count == 0) sb.Append("<p>No connected applications.</p>");
        foreach (var c in consents)
        {
            var host = HostOf(c.ClientUri);
            sb.Append("<div class=\"app\"><b>").Append(Pages.Encode(Short(c.ClientName))).Append("</b>");
            if (host is not null) sb.Append(" <span class=\"muted\">").Append(Pages.Encode(Short(host))).Append("</span>");
            sb.Append("<br><span class=\"muted\">Approved ").Append(Pages.Encode(When(c.CreatedAt)))
                .Append(" · Last used ").Append(Pages.Encode(When(c.LastUsedAt))).Append("</span>");
            sb.Append("<form method=\"post\" action=\"/connections/").Append(Pages.Encode(Uri.EscapeDataString(c.ClientId))).Append("/disconnect\">")
                .Append("<button type=\"submit\">Disconnect</button></form></div>");
        }
        return Pages.Html("Connected applications", sb.ToString());
    }

    private static string? HostOf(string? clientUri) =>
        Uri.TryCreate(clientUri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.Host : null;

    /// <summary>Approved-at is unknown (0) for a consent stored before the column existed.</summary>
    private static string When(DateTimeOffset t) =>
        t.ToUnixTimeMilliseconds() <= 0 ? "at an unknown time" : t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Short(string s) => s.Length <= MaxNameLength ? s : s[..MaxNameLength] + "…";
}
