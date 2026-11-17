using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace UnifiMcp;

/// <summary>
/// Wires the Claude connector (the public listener's OAuth side) into the host: the store, token issuer, client metadata
/// documents, keyring, Google sign-in, rate limits, the <c>McpJwt</c> bearer scheme and the SDK's protected-resource
/// metadata. Ported from Data Manager's McpHostingExtensions, without its API-key scheme or roles.
/// </summary>
public static class ConnectorHosting
{
    public const string JwtScheme = "McpJwt";
    public const string SigningKeyBroken = "signing key cannot be unprotected with the current keyring";

    /// <summary>
    /// Registers the connector. With values missing (<see cref="Settings.PublicMissing"/>) it registers only what needs
    /// none of them (the rate limiter) and marks the returned state not configured; the public listener then answers 503.
    /// </summary>
    /// <returns>The connector state the host reports and <see cref="PublicGate"/> consults.</returns>
    public static ConnectorState AddConnector(WebApplicationBuilder b, Settings s, HttpMessageHandler? google)
    {
        var state = new ConnectorState();
        b.Services.AddSingleton(state);
        // Before the auth handlers resolve it: their expiry checks follow this clock (tests register a fake one first).
        b.Services.TryAddSingleton(TimeProvider.System);
        AddRateLimits(b.Services);

        var missing = s.PublicMissing();
        if (missing.Count > 0)
        {
            state.NotConfigured(missing);
            return state;
        }

        var publicBase = s.PublicBaseUrl!.GetLeftPart(UriPartial.Authority);
        var resource = publicBase + "/mcp";

        DataKeys.AddProtectedDataProtection(b.Services, s, state);
        // Opening the store creates the folder, opens oauth.db and runs the schema, so it can fail. Nothing resolves it
        // while the host is built or started except the startup check, which turns a failure into a 503 on this side only.
        b.Services.AddSingleton<IOAuthStore>(sp => new SqliteOAuthStore(OAuthDbPath(s), sp.GetRequiredService<TimeProvider>()));
        b.Services.AddSingleton<ITokenIssuer>(sp => new TokenIssuer(sp.GetRequiredService<IOAuthStore>(), sp.GetRequiredService<IDataProtectionProvider>(),
            publicBase, resource, sp.GetRequiredService<TimeProvider>()));
        // Hosted services start in registration order: the check settles the state before the sweep first looks at it.
        b.Services.AddHostedService<StartupCheck>();
        b.Services.AddHostedService<OAuthSweepService>();
        b.Services.AddHttpClient(HttpClientMetadataDocuments.HttpClientName, c => c.Timeout = HttpClientMetadataDocuments.FetchTimeout)
            .ConfigurePrimaryHttpMessageHandler(HttpClientMetadataDocuments.CreateHandler);
        b.Services.AddSingleton<IClientMetadataDocuments, HttpClientMetadataDocuments>();

        if (s.SignIn == SignInMode.Code) b.Services.AddSingleton<SignInCodes>();
        var auth = b.Services.AddAuthentication();
        GoogleSignIn.Add(auth, s, google);
        auth.AddJwtBearer(JwtScheme, o =>
            {
                // Keep "email"/"scope" as literal claim types.
                o.MapInboundClaims = false;
                o.Events = new JwtBearerEvents
                {
                    // Load (and cache) the signing key before validation, outside the handler's catch-all: a keyring
                    // that cannot unwrap it surfaces as a CryptographicException and a 503, not as "invalid token".
                    OnMessageReceived = ctx => ctx.HttpContext.RequestServices.GetRequiredService<ITokenIssuer>().GetSigningKeyAsync(ctx.HttpContext.RequestAborted),
                };
            })
            .AddMcp(o =>
            {
                // The SDK forwards authentication to "Bearer" by default; ours is McpJwt. The challenge stays with the MCP
                // handler, which is what writes WWW-Authenticate: Bearer resource_metadata="…".
                o.ForwardAuthenticate = JwtScheme;
                var metadata = new ProtectedResourceMetadata { Resource = resource, ResourceName = "UniFi Network" };
                metadata.AuthorizationServers.Add(publicBase);
                metadata.ScopesSupported.Add("mcp:tools");
                if (!metadata.BearerMethodsSupported.Contains("header")) metadata.BearerMethodsSupported.Add("header");
                o.ResourceMetadata = metadata;
            });

        // Tokens minted by this server: fixed issuer and audience, the key looked up in-process, the host's clock.
        b.Services.AddOptions<JwtBearerOptions>(JwtScheme).Configure<ITokenIssuer, TimeProvider>((o, issuer, time) =>
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer.Issuer,
                ValidateAudience = true,
                ValidAudience = issuer.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                ValidTypes = ["at+jwt"],
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                NameClaimType = "email",
                RoleClaimType = "role",
                // The host's clock rather than DateTime.UtcNow, as the cookie and the store use (and tests can move).
                LifetimeValidator = (notBefore, expires, _, p) =>
                {
                    var now = time.GetUtcNow().UtcDateTime;
                    return expires is { } exp && now < exp.ToUniversalTime() + p.ClockSkew
                        && (notBefore is not { } nbf || nbf.ToUniversalTime() - p.ClockSkew <= now);
                },
                // Already loaded by OnMessageReceived, so this returns the cached key at once.
                IssuerSigningKeyResolver = (_, _, _, _) => [issuer.GetSigningKeyAsync().GetAwaiter().GetResult()],
            });
        return state;
    }

    /// <summary>
    /// Public-listener middleware, after <see cref="PublicGate"/>: a stored signing key the keyring cannot unwrap marks the
    /// connector broken and answers 503 (now and from then on), and the rate limits apply.
    /// </summary>
    public static void UseConnector(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.ListenerOf() != Listener.Public)
            {
                await next(ctx);
                return;
            }
            try
            {
                await next(ctx);
            }
            catch (CryptographicException) when (!ctx.Response.HasStarted)
            {
                if (!await SigningKeyUnreadableAsync(ctx.RequestServices)) throw;
                var state = ctx.RequestServices.GetRequiredService<ConnectorState>();
                state.Broken(SigningKeyBroken);
                app.Logger.LogError("Connector unavailable: {Reason}. The keyring in {Dir} is not the one that wrapped the key in oauth.db.",
                    SigningKeyBroken, DataKeys.KeysDir(ctx.RequestServices.GetRequiredService<Settings>()));
                ctx.Response.Clear();
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });
        app.UseRateLimiter();
    }

    public static string OAuthDbPath(Settings s) => Path.Combine(s.DataDir, "oauth.db");

    /// <summary>Maps the OAuth routes; a public side that is not configured maps none of them.</summary>
    public static void MapConnector(WebApplication app)
    {
        // The state, not the issuer: resolving the issuer would open oauth.db while the host is still being built.
        if (!app.Services.GetRequiredService<ConnectorState>().Configured) return;
        OAuthEndpoints.Map(app);
        ConnectionsPage.Map(app);
        if (app.Services.GetRequiredService<Settings>().SignIn == SignInMode.Code) CodeSignIn.Map(app);
    }

    /// <summary>Whether the failure is the signing key: anything else is rethrown as it was.</summary>
    private static async Task<bool> SigningKeyUnreadableAsync(IServiceProvider services)
    {
        if (services.GetService<ITokenIssuer>() is not { } issuer) return false;
        try
        {
            await issuer.GetSigningKeyAsync();
            return false;
        }
        catch (CryptographicException)
        {
            return true;
        }
    }

    // ---- rate limits (Data Manager's numbers), public listener only, per Cloudflare client address ----

    private static void AddRateLimits(IServiceCollection services) => services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        // Registration is unauthenticated by design (RFC 7591), so it is the tightest; the token endpoint sees one call
        // per hour per connector in steady state.
        o.AddPolicy(OAuthEndpoints.RegisterRateLimitPolicy, ctx => Partition(ctx, 10, TimeSpan.FromHours(1), 6));
        o.AddPolicy(OAuthEndpoints.TokenRateLimitPolicy, ctx => Partition(ctx, 60, TimeSpan.FromMinutes(10), 5));
        o.AddPolicy(OAuthEndpoints.AuthorizeRateLimitPolicy, ctx => Partition(ctx, 30, TimeSpan.FromMinutes(10), 5));
        // The code page: the owner needs a handful of requests; the per-hour cap on new codes is SignInCodes' own.
        o.AddPolicy(CodeSignIn.RateLimitPolicy, ctx => Partition(ctx, 20, TimeSpan.FromMinutes(10), 5));
        o.OnRejected = async (context, ct) =>
        {
            var http = context.HttpContext;
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnifiMcp.OAuth")
                .LogWarning("Rate limit exceeded for {Ip} on {Path}", OAuthEndpoints.ClientIp(http), LogText.Clean(http.Request.Path.Value));
            await http.Response.WriteAsJsonAsync(new { error = "Too many requests. Please try again later." }, ct);
        };
    });

    private static RateLimitPartition<string> Partition(HttpContext ctx, int permits, TimeSpan window, int segments) =>
        ctx.ListenerOf() != Listener.Public
            ? RateLimitPartition.GetNoLimiter("not-public")
            : RateLimitPartition.GetSlidingWindowLimiter(OAuthEndpoints.ClientIp(ctx), _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                SegmentsPerWindow = segments,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            });

    /// <summary>
    /// At startup: logs a reason found while the host was built (an unreadable keys folder), opens oauth.db, and unwraps
    /// a signing key it already holds. A store that cannot be opened (a corrupt file, wrong ownership, a full disk) or a
    /// keyring that was lost or replaced (the data-key check passes on an empty keys folder) marks the public side broken
    /// before the first request; the tailnet side never needs either. A missing key is left to be generated on first use.
    /// </summary>
    private sealed class StartupCheck(IServiceProvider services, Settings settings, ConnectorState state, ILogger<StartupCheck> log) : IHostedService
    {
        public async Task StartAsync(CancellationToken ct)
        {
            if (state.UnloggedReason is { } early) log.LogWarning("Connector unavailable: {Reason}.", early);
            if (!state.Ready) return;

            SigningKey? stored;
            try
            {
                stored = await services.GetRequiredService<IOAuthStore>().GetSigningKeyAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The path and the exception type: enough to act on, and no stack trace for a known condition.
                var reason = $"cannot open {OAuthDbPath(settings)} ({e.GetType().Name})";
                state.Broken(reason);
                log.LogWarning("Connector unavailable: {Reason}.", reason);
                return;
            }
            if (stored is null) return;

            try
            {
                await services.GetRequiredService<ITokenIssuer>().GetSigningKeyAsync(ct);
            }
            catch (CryptographicException)
            {
                state.Broken(SigningKeyBroken);
                log.LogError("Connector unavailable: {Reason}.", SigningKeyBroken);
            }
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
