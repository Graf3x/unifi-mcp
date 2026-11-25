using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace UnifiMcp;

/// <summary>
/// unifi-mcp as the OAuth 2.1 authorization server for its own public <c>/mcp</c> (ported from Data Manager's
/// McpOAuthEndpoints). A client registers here (RFC 7591) or names itself with a client metadata document, the owner
/// signs in (the <c>unifi.auth</c> cookie: Google against the allow-list, or a code from the log), consents once per client, and this server
/// issues its own tokens that the <c>McpJwt</c> scheme validates. Registration alone grants nothing.
/// </summary>
public static class OAuthEndpoints
{
    public const string RegisterRateLimitPolicy = "oauth-register";
    public const string TokenRateLimitPolicy = "oauth-token";
    public const string AuthorizeRateLimitPolicy = "oauth-authorize";
    internal static readonly string[] AllowedScopes = ["mcp:tools"];
    internal const int MaxRegistrationBytes = 16 * 1024;
    internal static readonly TimeSpan PendingConsentLifetime = TimeSpan.FromMinutes(10);
    private const string ConsentProtectorPurpose = "UnifiMcp.OAuth.PendingConsent.v1";
    private const int MaxProblemLength = 300;
    private const string LogCategory = "UnifiMcp.OAuth";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void Map(WebApplication app)
    {
        // ---- discovery ------------------------------------------------------------------------------
        app.MapGet("/.well-known/oauth-authorization-server", (ITokenIssuer issuer) => Results.Json(Metadata(issuer), Json));
        // Some clients look here first; same document.
        app.MapGet("/.well-known/openid-configuration", (ITokenIssuer issuer) => Results.Json(Metadata(issuer), Json));
        app.MapGet("/oauth/jwks", async (ITokenIssuer issuer, CancellationToken ct) => Results.Json(await issuer.GetJwksAsync(ct), Json));

        // ---- RFC 7591 dynamic client registration -----------------------------------------------------
        app.MapPost("/oauth/register", async Task<IResult> (HttpContext http, Clients clients, ILoggerFactory lf, CancellationToken ct) =>
            {
                // Anyone may call this, so the body is read only up to the cap: a declared length over it is refused
                // unread, and a chunked one stops being read as soon as it proves larger.
                IResult TooLarge() => OAuthError(StatusCodes.Status413PayloadTooLarge, "invalid_client_metadata",
                    $"The client metadata document must be at most {MaxRegistrationBytes / 1024} KB.");
                if (!http.Request.HasJsonContentType()) return OAuthError(400, "invalid_client_metadata", "Body must be a JSON client metadata document.");
                if (http.Request.ContentLength is > MaxRegistrationBytes) return TooLarge();
                using var body = await HttpClientMetadataDocuments.ReadBoundedAsync(http.Request.Body, MaxRegistrationBytes, ct);
                if (body is null) return TooLarge();

                RegistrationRequest? request;
                try { request = await JsonSerializer.DeserializeAsync<RegistrationRequest>(body, Json, ct); }
                catch (JsonException) { request = null; }
                if (request is null) return OAuthError(400, "invalid_client_metadata", "Body must be a JSON client metadata document.");

                var outcome = await clients.RegisterAsync(request, $"{ClientIp(http)} {Trim(http.Request.Headers.UserAgent.ToString(), 120)}", ct);
                if (outcome is RegistrationRefused refused) return OAuthError(400, refused.Error, refused.Description);
                var client = ((Registered)outcome).Registration;
                Log(lf).LogInformation("OAuth: client {ClientId} '{Name}' registered from {From}", client.ClientId, LogText.Clean(client.ClientName),
                    LogText.Clean(client.RegisteredFrom));

                return Results.Json(new
                {
                    client_id = client.ClientId,
                    client_id_issued_at = client.CreatedAt.ToUnixTimeSeconds(),
                    client_name = client.ClientName,
                    redirect_uris = client.RedirectUris,
                    client_uri = client.ClientUri,
                    logo_uri = client.LogoUri,
                    token_endpoint_auth_method = "none",
                    grant_types = new[] { "authorization_code", "refresh_token" },
                    response_types = new[] { "code" },
                    scope = string.Join(' ', AllowedScopes),
                }, Json, statusCode: StatusCodes.Status201Created);
            })
            .RequireRateLimiting(RegisterRateLimitPolicy);

        // ---- authorize ------------------------------------------------------------------------------
        app.MapGet("/oauth/authorize", async Task<IResult> (HttpContext http, Clients clients, ITokenIssuer issuer, GrantLedger ledger,
                IDataProtectionProvider dp, Settings settings, TimeProvider time, ILoggerFactory lf, CancellationToken ct) =>
            {
                var q = http.Request.Query;
                var clientId = q["client_id"].ToString();
                var redirectUri = q["redirect_uri"].ToString();

                // Until the client and redirect URI are known-good nothing may be redirected: render the error.
                var (client, clientProblem) = await clients.ResolveAsync(clientId, ct);
                if (client is null)
                    return Page(400, Pages.ErrorPage("Unknown client", clientProblem == Clients.Unknown
                        ? "This application is not registered with UniFi Network. Reconnect it and try again."
                        : $"This application's identity could not be verified: {Truncate(clientProblem)}"));
                if (string.IsNullOrEmpty(redirectUri) || !client.Allows(redirectUri))
                    return Page(400, Pages.ErrorPage("Redirect not allowed", "The application asked to send you to an address it did not register."));

                var state = q["state"].ToString();
                IResult Fail(string error, string description) => RedirectWithError(issuer, redirectUri, error, description, state);

                if (q["response_type"].ToString() != "code") return Fail("unsupported_response_type", "Only response_type=code is supported.");
                var challenge = q["code_challenge"].ToString();
                if (challenge.Length is < 43 or > 128 || q["code_challenge_method"].ToString() != "S256")
                    return Fail("invalid_request", "PKCE with code_challenge_method=S256 is required.");
                var scope = NormalizeScope(q["scope"].ToString());
                if (scope is null) return Fail("invalid_scope", $"Supported scopes: {string.Join(' ', AllowedScopes)}.");
                var resource = q["resource"].ToString();
                if (!ResourceAllowed(resource, issuer)) return Fail("invalid_target", "This server issues tokens for its own MCP endpoint only.");

                // The owner: the sign-in cookie (Google or code; its validation re-checks the owner on every use).
                var auth = await http.AuthenticateAsync(GoogleSignIn.CookieScheme);
                if (!auth.Succeeded || auth.Principal is null)
                {
                    await http.ChallengeAsync(GoogleSignIn.CookieScheme);
                    return Results.Empty;
                }
                // Cookie validation already admitted the owner; a cookie with no subject never gets this far, but is refused.
                if (OAuthUser.From(auth.Principal) is not { } user)
                {
                    Log(lf).LogWarning("OAuth: authorize refused for {Email} ({Reason}) client {ClientId} from {Ip}",
                        LogText.Clean(auth.Principal.FindFirst("email")?.Value) ?? "(no email)", "no subject", LogText.Clean(client.ClientId), ClientIp(http));
                    return Fail("access_denied", settings.SignIn == SignInMode.Code
                        ? "This sign-in isn't allowed to use UniFi Network."
                        : "This Google account isn't allowed to use UniFi Network.");
                }

                // Consent once per client: a standing consent skips the page and keeps its family; otherwise the owner is
                // asked, and the consent POST starts a fresh family.
                if (await ledger.AuthorizeAsync(user, client, new CodeRequest(redirectUri, challenge, scope, NullIfEmpty(resource)), ct) is { } code)
                    return CodeRedirect(issuer, redirectUri, code, state);

                var pending = new PendingAuthorization(clientId, redirectUri, challenge, scope, NullIfEmpty(resource), state, user.Subject,
                    time.GetUtcNow().Add(PendingConsentLifetime));
                var token = dp.CreateProtector(ConsentProtectorPurpose).Protect(JsonSerializer.Serialize(pending, Json));
                return ConsentPage(client, user, token, redirectUri, settings);
            })
            .RequireRateLimiting(AuthorizeRateLimitPolicy);

        app.MapPost("/oauth/consent", async Task<IResult> (HttpContext http, Clients clients, ITokenIssuer issuer, GrantLedger ledger, IDataProtectionProvider dp,
                TimeProvider time, ILoggerFactory lf, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Page(400, Pages.ErrorPage("This request has expired", "Go back to the application and connect again."));
                var form = await http.Request.ReadFormAsync(ct);
                PendingAuthorization? pending;
                try { pending = JsonSerializer.Deserialize<PendingAuthorization>(dp.CreateProtector(ConsentProtectorPurpose).Unprotect(form["request"].ToString()), Json); }
                catch (Exception) { pending = null; }
                if (pending is null || pending.ExpiresAt < time.GetUtcNow())
                    return Page(400, Pages.ErrorPage("This request has expired", "Go back to the application and connect again."));

                var auth = await http.AuthenticateAsync(GoogleSignIn.CookieScheme);
                var user = auth.Succeeded && auth.Principal is not null ? OAuthUser.From(auth.Principal) : null;
                // The consent must be given by the same owner the request was rendered for. Cookie validation already
                // admitted whoever is signed in; a removed address has no cookie left, so no user.
                if (user is null || user.Subject != pending.UserSubject)
                {
                    Log(lf).LogWarning("OAuth: consent refused for {Email}: not the signed-in, allowed owner (from {Ip})", LogText.Clean(user?.Label) ?? "(none)", ClientIp(http));
                    return Page(400, Pages.ErrorPage("Sign-in changed", "The signed-in account is not the one this request was for. Connect again."));
                }

                var (client, _) = await clients.ResolveAsync(pending.ClientId, ct);
                if (client is null || !client.Allows(pending.RedirectUri))
                    return Page(400, Pages.ErrorPage("Unknown client", "This application is no longer registered."));

                if (form["decision"].ToString() != "allow")
                {
                    Log(lf).LogInformation("OAuth: {Email} denied client {ClientId}", LogText.Clean(user.Label), LogText.Clean(client.ClientId));
                    return RedirectWithError(issuer, pending.RedirectUri, "access_denied", "The user declined.", pending.State);
                }

                var code = await ledger.ApproveAsync(user, client, new CodeRequest(pending.RedirectUri, pending.CodeChallenge, pending.Scope, pending.Resource), ct);
                Log(lf).LogInformation("OAuth: {Email} granted client {ClientId} '{Name}' scope {Scope}", LogText.Clean(user.Label), LogText.Clean(client.ClientId),
                    LogText.Clean(client.DisplayName), pending.Scope);
                return CodeRedirect(issuer, pending.RedirectUri, code, pending.State);
            });

        // ---- token ---------------------------------------------------------------------------------
        app.MapPost("/oauth/token", async Task<IResult> (HttpContext http, Clients clients, ITokenIssuer issuer, GrantLedger ledger,
                ILoggerFactory lf, CancellationToken ct) =>
            {
                var log = Log(lf);
                var clientId = "";
                // Every refusal is one log line: what was wrong, which client, from where. Never the code, token or verifier.
                IResult Refuse(int status, string error, string description, string? email = null, string? reason = null)
                {
                    // The description can echo a client's metadata document, and the client_id is whatever the form said.
                    log.LogWarning("OAuth: token request refused ({Error}: {Reason}) for {Email} client {ClientId} from {Ip}",
                        error, LogText.Clean(reason ?? description), LogText.Clean(email) ?? "(unknown)",
                        LogText.Clean(Truncate(clientId)) is { Length: > 0 } c ? c : "(none)", ClientIp(http));
                    return OAuthError(status, error, description);
                }

                // RFC 6749 §5.1: token responses (and their errors) are never cached.
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.Pragma = "no-cache";
                if (!http.Request.HasFormContentType) return Refuse(400, "invalid_request", "Send application/x-www-form-urlencoded.");
                var form = await http.Request.ReadFormAsync(ct);
                var grant = form["grant_type"].ToString();
                clientId = form["client_id"].ToString();
                var (client, clientProblem) = await clients.ResolveAsync(clientId, ct);
                if (client is null) return Refuse(401, "invalid_client", Truncate(clientProblem) ?? Clients.Unknown);
                // Checked before a code or refresh token is spent, so a client that asked for the wrong audience can still retry.
                if (!ResourceAllowed(form["resource"].ToString(), issuer))
                    return Refuse(400, "invalid_target", "This server issues tokens for its own MCP endpoint only.");

                switch (grant)
                {
                    case "authorization_code":
                    {
                        var code = form["code"].ToString();
                        var verifier = form["code_verifier"].ToString();
                        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(verifier)) return Refuse(400, "invalid_request", "code and code_verifier are required.");

                        var outcome = await ledger.ExchangeAsync(code, verifier, form["redirect_uri"].ToString(), client, ct);
                        if (outcome is GrantRefused refused)
                            return Refuse(400, "invalid_grant", refused.Description, refused.Label, refused.Reason);
                        var issued = (GrantIssued)outcome;
                        await clients.RecordUseAsync(client, ct);
                        log.LogInformation("OAuth: issued tokens to {Email} for client {ClientId} from {Ip}", LogText.Clean(issued.User.Label),
                            LogText.Clean(client.ClientId), ClientIp(http));
                        return await TokenResponseAsync(issuer, issued.User, client.ClientId, issued.Scope, issued.RefreshToken, ct);
                    }
                    case "refresh_token":
                    {
                        var presented = form["refresh_token"].ToString();
                        if (string.IsNullOrEmpty(presented)) return Refuse(400, "invalid_request", "refresh_token is required.");
                        var outcome = await ledger.RotateAsync(presented, client, ct);
                        if (outcome is GrantRefused refused)
                            return Refuse(400, "invalid_grant", refused.Description, refused.Label, refused.Reason);
                        var issued = (GrantIssued)outcome;
                        await clients.RecordUseAsync(client, ct);
                        log.LogInformation("OAuth: refreshed tokens for {Email} client {ClientId} from {Ip}", LogText.Clean(issued.User.Label),
                            LogText.Clean(client.ClientId), ClientIp(http));
                        return await TokenResponseAsync(issuer, issued.User, client.ClientId, issued.Scope, issued.RefreshToken, ct);
                    }
                    default:
                        return Refuse(400, "unsupported_grant_type", "Supported: authorization_code, refresh_token.");
                }
            })
            .RequireRateLimiting(TokenRateLimitPolicy);

        // RFC 7009: a client giving back its refresh token revokes its whole family.
        app.MapPost("/oauth/revoke", async Task<IResult> (HttpContext http, GrantLedger ledger, ILoggerFactory lf, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.Ok();
                var form = await http.Request.ReadFormAsync(ct);
                var token = form["token"].ToString();
                if (!string.IsNullOrEmpty(token) && await ledger.RevokeAsync(token, ct) is { } revoked)
                    Log(lf).LogInformation("OAuth: revoked access for {Email} client {ClientId} from {Ip}", LogText.Clean(revoked.User.Label),
                        LogText.Clean(revoked.ClientId), ClientIp(http));
                return Results.Ok(); // RFC 7009: always 200, say nothing about unknown tokens
            })
            .RequireRateLimiting(TokenRateLimitPolicy);
    }

    // ---------------------------------------------------------------------------------------------------- helpers

    private static object Metadata(ITokenIssuer issuer) => new
    {
        issuer = issuer.Issuer,
        authorization_endpoint = issuer.Issuer + "/oauth/authorize",
        token_endpoint = issuer.Issuer + "/oauth/token",
        registration_endpoint = issuer.Issuer + "/oauth/register",
        revocation_endpoint = issuer.Issuer + "/oauth/revoke",
        jwks_uri = issuer.Issuer + "/oauth/jwks",
        response_types_supported = new[] { "code" },
        response_modes_supported = new[] { "query" },
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        code_challenge_methods_supported = new[] { "S256" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        // Client ID Metadata Documents: Claude's hosted surfaces and Claude Code send a URL as client_id instead of
        // registering. Requires "none" above as well; both are checked by the client.
        client_id_metadata_document_supported = true,
        // RFC 9207: every redirect back carries iss, so a client can tell this server's answer from another's.
        authorization_response_iss_parameter_supported = true,
        revocation_endpoint_auth_methods_supported = new[] { "none" },
        scopes_supported = AllowedScopes,
        subject_types_supported = new[] { "public" },
        id_token_signing_alg_values_supported = new[] { "RS256" },
    };

    /// <summary>No resource, or exactly this server's MCP endpoint (a trailing slash and case are forgiven).</summary>
    private static bool ResourceAllowed(string resource, ITokenIssuer issuer) =>
        string.IsNullOrEmpty(resource) || string.Equals(resource.TrimEnd('/'), issuer.Audience.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Back to the client with its code; the URL carries the code, so it is never logged.</summary>
    /// <summary>Back to the client with a code, and this server named as its issuer (RFC 9207).</summary>
    private static IResult CodeRedirect(ITokenIssuer issuer, string redirectUri, string code, string state) =>
        new QuietRedirect(WithQuery(redirectUri, ("code", code), ("state", state), ("iss", issuer.Issuer)));

    private static async Task<IResult> TokenResponseAsync(ITokenIssuer issuer, OAuthUser user, string clientId, string scope, string refreshToken, CancellationToken ct)
    {
        var access = await issuer.IssueAccessTokenAsync(user, clientId, scope, ct);
        return Results.Json(new
        {
            access_token = access,
            token_type = "Bearer",
            expires_in = (int)issuer.AccessTokenLifetime.TotalSeconds,
            refresh_token = refreshToken,
            scope,
        }, Json);
    }

    internal static string? NormalizeScope(string requested)
    {
        var parts = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return string.Join(' ', AllowedScopes);
        return parts.All(p => AllowedScopes.Contains(p, StringComparer.Ordinal)) ? string.Join(' ', parts.Distinct()) : null;
    }

    /// <summary>The caller's address as Cloudflare saw it; only the public listener serves these routes. Logged, so cleaned.</summary>
    internal static string ClientIp(HttpContext http) =>
        http.Request.Headers["CF-Connecting-IP"].ToString() is { Length: > 0 and <= 64 } ip ? LogText.Clean(ip) : "unknown";

    /// <summary>The caller's user agent, for the registration's record: trimmed and capped.</summary>
    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    /// <summary>Problems can echo a client's own document text: shown or returned only this long.</summary>
    private static string? Truncate(string? s) => s is null || s.Length <= MaxProblemLength ? s : s[..MaxProblemLength] + "…";

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static ILogger Log(ILoggerFactory lf) => lf.CreateLogger(LogCategory);

    private static IResult OAuthError(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    /// <summary>Back to the client with an error, and this server named as its issuer (RFC 9207).</summary>
    private static IResult RedirectWithError(ITokenIssuer issuer, string redirectUri, string error, string description, string state) =>
        new QuietRedirect(WithQuery(redirectUri, ("error", error), ("error_description", description), ("state", state), ("iss", issuer.Issuer)));

    /// <summary>
    /// A 302 that writes no log line. Results.Redirect logs its destination at Information, and the destination carries
    /// the authorization code.
    /// </summary>
    private sealed class QuietRedirect(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status302Found;
            http.Response.Headers.Location = url;
            http.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }
    }

    /// <summary>The redirect URI with these parameters added. A query the URI was registered with is kept (RFC 6749
    /// 3.1.2), less any parameter of the same name, so the answer carries one code, state and iss: ours.</summary>
    private static string WithQuery(string baseUri, params (string Key, string? Value)[] parameters)
    {
        var mark = baseUri.IndexOf('?');
        var kept = mark < 0 ? [] : baseUri[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !parameters.Any(n => n.Key == Uri.UnescapeDataString(p.Split('=', 2)[0].Replace('+', ' '))));
        var added = parameters.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!));
        return (mark < 0 ? baseUri : baseUri[..mark]) + "?" + string.Join('&', kept.Concat(added));
    }

    private static IResult Page(int status, string html, string? formTarget = null) => new PageResult(status, html, formTarget);

    private sealed class PageResult(int status, string html, string? formTarget) : IResult
    {
        public Task ExecuteAsync(HttpContext http) => Pages.WriteAsync(http.Response, status, html, formTarget);
    }

    private static IResult ConsentPage(Client client, OAuthUser user, string requestToken, string redirectUri, Settings settings)
    {
        var redirect = Uri.TryCreate(redirectUri, UriKind.Absolute, out var x) ? x : null;
        var host = redirect?.Host ?? redirectUri;
        var loopback = redirect is not null && Clients.IsLoopback(redirect);
        var who = client.FromMetadataDocument
            ? $"An application published by <b>{Pages.Encode(client.DisplayName)}</b>"
            : $"<b>{Pages.Encode(client.DisplayName)}</b>";
        var can = settings.AllowWrites
            ? "Read your UniFi network's devices, clients and settings, and change them. Each change shows a preview and needs a separate confirmation."
            : "Read your UniFi network's devices, clients and settings. It cannot change anything.";
        var connections = settings.PublicBaseUrl is { } b ? new Uri(b, "/connections").ToString() : "/connections";
        var body = $"""
            <h1>Connect to UniFi Network</h1>
            <p>{who} wants to use the UniFi Network tools as you.</p>
            <dl>
              <dt>Signed in as</dt><dd>{Pages.Encode(user.Label)}</dd>
              <dt>The application will send you back to</dt><dd><code>{Pages.Encode(loopback ? $"{host} (a program running on your own computer, port {redirect!.Port})" : host)}</code></dd>
              <dt>It will be able to</dt><dd>{Pages.Encode(can)}</dd>
            </dl>
            <p>You can disconnect it at any time from {Pages.Encode(connections)}. If you did not start this from an application you trust, choose Deny.</p>
            <form method="post" action="/oauth/consent">
              <input type="hidden" name="request" value="{Pages.Encode(requestToken)}">
              <button type="submit" name="decision" value="allow">Allow</button>
              <button type="submit" name="decision" value="deny">Deny</button>
            </form>
            """;
        // The consent POST answers with a redirect to the client, and browsers hold that redirect to form-action too.
        return Page(200, Pages.Html("Connect to UniFi Network", body), redirect?.GetLeftPart(UriPartial.Authority));
    }

    internal sealed record PendingAuthorization(string ClientId, string RedirectUri, string CodeChallenge, string Scope, string? Resource, string State,
        string UserSubject, DateTimeOffset ExpiresAt);

}
