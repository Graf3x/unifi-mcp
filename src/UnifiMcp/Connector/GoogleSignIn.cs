using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace UnifiMcp;

/// <summary>
/// The owner's browser sign-in into the <c>unifi.auth</c> cookie: Google OIDC (code flow, PKCE, query callback) by
/// default, or a one-time code from the log with MCP_SIGNIN=code (<see cref="CodeSignIn"/>). With Google, only a verified
/// address the owner admission accepts gets the cookie, and every use of the cookie asks <see cref="OwnerAdmission"/> again,
/// so removing an address signs it out at once. The cookie is for the owner's pages (/oauth/authorize, /connections)
/// only: /mcp never accepts it.
/// </summary>
public static class GoogleSignIn
{
    public const string CookieScheme = "unifi.auth";
    public const string GoogleScheme = "google";
    /// <summary>The authorization policy for the owner's pages: the cookie scheme only.</summary>
    public const string Policy = "google-owner";
    public const string CallbackPath = "/signin-google";

    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// When the owner signed in with Google. Sliding renewal re-stamps the ticket's IssuedUtc, so the 90-day limit is
    /// measured from this instead.
    /// </summary>
    private const string SignedInKey = ".unifi.signed_in";

    /// <summary>
    /// A browser navigating to an owner page (<see cref="RouteTable.SendsToSignIn"/>) is sent to sign-in: Google, or the
    /// code page. Anything else (an API call, a POST) gets a bare 401.
    /// </summary>
    internal static bool SendsToGoogle(string method, PathString path) =>
        (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) && RouteTable.SendsToSignIn(path);

    public static void Add(AuthenticationBuilder b, Settings s, HttpMessageHandler? backchannel)
    {
        var publicBase = s.PublicBaseUrl ?? throw new InvalidOperationException("MCP_PUBLIC_BASE_URL is not set.");
        // The public socket's requests arrive as plain http from the funnel, so the callback is pinned to the public URL.
        var redirectUri = new Uri(publicBase, CallbackPath).ToString();

        b.Services.AddAuthorization(o => o.AddPolicy(Policy, p => p.AddAuthenticationSchemes(CookieScheme).RequireAuthenticatedUser()));

        b.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = CookieScheme;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = SlidingLifetime;
            o.SlidingExpiration = true;
            o.LoginPath = CallbackPath;
            o.Events = new CookieAuthenticationEvents
            {
                OnSigningIn = ctx =>
                {
                    ctx.Properties.IsPersistent = true;
                    ctx.Properties.Items[SignedInKey] = Now(ctx.Options).ToString("O", CultureInfo.InvariantCulture);
                    return Task.CompletedTask;
                },
                OnValidatePrincipal = ValidatePrincipalAsync,
                OnRedirectToLogin = ctx =>
                {
                    if (!SendsToGoogle(ctx.Request.Method, ctx.Request.Path))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    if (string.IsNullOrEmpty(ctx.Properties.RedirectUri))
                        ctx.Properties.RedirectUri = ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
                    // Code mode: to the code page, which comes back to the page that asked.
                    if (s.SignIn == SignInMode.Code)
                    {
                        ctx.Response.Redirect(CodeSignIn.Path + "?returnUrl=" + Uri.EscapeDataString(ctx.Properties.RedirectUri));
                        return Task.CompletedTask;
                    }
                    // Straight to Google, coming back to the page that asked.
                    return ctx.HttpContext.ChallengeAsync(GoogleScheme, ctx.Properties);
                },
                OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                },
            };
        });

        // Code mode has no Google client: /signin-google is not served at all.
        if (s.SignIn != SignInMode.Google) return;
        b.AddOpenIdConnect(GoogleScheme, o =>
        {
            o.Authority = s.GoogleAuthority.ToString().TrimEnd('/');
            o.ClientId = s.GoogleClientId;
            o.ClientSecret = s.GoogleClientSecret;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("email");
            o.Scope.Add("profile");
            o.CallbackPath = CallbackPath;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.SaveTokens = false;
            o.SignInScheme = CookieScheme;
            o.SignOutScheme = CookieScheme;
            // The callback is a top-level GET from Google, which carries Lax cookies.
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            if (backchannel is not null) o.BackchannelHttpHandler = backchannel;
            o.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = ctx =>
                {
                    ctx.ProtocolMessage.RedirectUri = redirectUri;
                    // ASP.NET leaves response_mode out when it is the default for code; say it, so the callback is a GET.
                    ctx.ProtocolMessage.ResponseMode = OpenIdConnectResponseMode.Query;
                    return Task.CompletedTask;
                },
                OnTokenValidated = TokenValidatedAsync,
                OnRemoteFailure = async ctx =>
                {
                    // IdentityModel hides token contents in its messages (PII and security artifacts are off by default). The
                    // message can quote the callback's error and error_description, which anyone holding a state can write.
                    Log(ctx.HttpContext).LogWarning("Google sign-in failed: {Failure}", LogText.Clean(ctx.Failure?.Message) ?? "unknown");
                    ctx.HandleResponse();
                    await Pages.WriteAsync(ctx.Response, StatusCodes.Status400BadRequest,
                        Pages.ErrorPage("Google sign-in didn't finish", "Go back to Claude and start again."));
                },
                // Nothing here signs out through Google: those endpoints fall through to a 404.
                OnRemoteSignOut = ctx =>
                {
                    ctx.SkipHandler();
                    return Task.CompletedTask;
                },
                OnSignedOutCallbackRedirect = ctx =>
                {
                    ctx.SkipHandler();
                    return Task.CompletedTask;
                },
            };
        });
    }

    private static async Task TokenValidatedAsync(TokenValidatedContext ctx)
    {
        var google = ctx.Principal ?? new ClaimsPrincipal();
        var settings = ctx.HttpContext.RequestServices.GetRequiredService<Settings>();
        var email = google.FindFirst("email")?.Value.Trim();
        // Only here is Google's own email_verified seen (a JSON boolean arrives as "true"; a string "true" is accepted too):
        // an identity exists only for an address Google verified, so owner admission afterwards never asks about it.
        var reason = string.IsNullOrEmpty(email) ? "NoEmail"
            : !string.Equals(google.FindFirst("email_verified")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase) ? "Unverified"
            : OwnerAdmission.Admit(new OAuthUser { Subject = google.FindFirst("sub")?.Value ?? "", Email = email }, settings).ToString();
        var log = Log(ctx.HttpContext);
        if (reason != nameof(Admission.Allowed))
        {
            log.LogWarning("Google sign-in refused for {Email}: {Reason}", email is { Length: > 0 } ? email : "(no email)", reason);
            ctx.HandleResponse(); // no cookie; the correlation and nonce cookies were already deleted when they were read
            await Pages.WriteAsync(ctx.Response, StatusCodes.Status403Forbidden, Pages.ErrorPage("This Google account isn't allowed",
                reason == "Unverified"
                    ? "Google hasn't verified this account's email address."
                    : $"{(email is { Length: > 0 } ? email : "This account")} isn't on this server's list of owners. Sign in with the owner's Google account."));
            return;
        }

        log.LogInformation("Google sign-in for {Email}: {Reason}", email, reason);
        // Only what the owner's pages need.
        var identity = new ClaimsIdentity(
            new[] { Claim("sub", google), new Claim("email", email!), Claim("name", google) }.OfType<Claim>(),
            CookieScheme, "email", null);
        ctx.Principal = new ClaimsPrincipal(identity);
    }

    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal ?? new ClaimsPrincipal();
        var settings = ctx.HttpContext.RequestServices.GetRequiredService<Settings>();
        // A cookie without a subject is no identity at all; otherwise the owner check runs again on every use.
        string? reason = OAuthUser.From(principal) is not { } user ? "no subject"
            : OwnerAdmission.Admit(user, settings) is var admission && admission != Admission.Allowed ? admission.ToString()
            : null;
        if (reason is null)
        {
            var signedIn = ctx.Properties.Items.TryGetValue(SignedInKey, out var raw)
                && DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at : ctx.Properties.IssuedUtc;
            if (signedIn is null || Now(ctx.Options) - signedIn.Value > AbsoluteLifetime) reason = "signed in more than 90 days ago";
        }
        if (reason is null) return;

        Log(ctx.HttpContext).LogInformation("Signed {Email} out: {Reason}", principal.FindFirst("email")?.Value ?? "(no email)", reason);
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieScheme);
    }

    private static Claim? Claim(string type, ClaimsPrincipal from) =>
        from.FindFirst(type)?.Value is { Length: > 0 } value ? new Claim(type, value) : null;

    private static DateTimeOffset Now(AuthenticationSchemeOptions o) => (o.TimeProvider ?? TimeProvider.System).GetUtcNow();

    private static ILogger Log(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GoogleSignIn).FullName!);
}
