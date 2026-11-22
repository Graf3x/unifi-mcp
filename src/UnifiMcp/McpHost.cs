using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>Builds the server: Unix socket or dev listener, Tailscale-User-Login caller identity, stateless MCP over HTTP.</summary>
public static class McpHost
{
    public const string Instructions =
        """
        You are talking to a UniFi Network console through its official API (v11.0.97), plus read-only diagnostics from its internal API. Treat it as sensitive.

        Conventions:
        - Call getting_started first: it says whether the server is configured and whether writes are on.
        - Prefer the *_faq tools for questions (who is on a network, what path a client takes, is a DHCP reservation honoured, network, internet, Wi-Fi, client and device health). For one known lookup use call_read_operation, which runs any read operation by name.
        - For "what happened" use system_log; call it once without filters to see counts, then filter by category, event, severity or search.
        - The diagnostics tools are rate limited to protect the console. Call each once per question; do not poll or loop on them.
        - search_operations finds an operation (with its parameters) and describe_operation shows its full schema.
        - siteId is optional and takes a UUID or a name; it defaults to the configured site.
        - Tool descriptions are prefixed with their category, e.g. [Firewall]. Lists take offset, limit (max 200) and filter.
        - call_read_operation returns the console's JSON. Answers may be truncated; narrow with filter rather than paging blindly.
        - Writes are off unless the owner enabled them. A write tool is marked destructive and needs confirm set to its own tool name; without that it returns a preview and sends nothing. Show the owner the preview before confirming.
        """;

    /// <param name="listenerOf">Tests only: replaces <see cref="Listeners.Of"/>, because a TestServer has no endpoint.</param>
    /// <param name="google">Tests only: the Google OIDC backchannel (discovery, JWKS, token endpoint).</param>
    public static WebApplication Build(Settings settings, HttpMessageHandler? unifi = null, Action<WebApplicationBuilder>? configure = null,
        Func<HttpContext, Listener>? listenerOf = null, HttpMessageHandler? google = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        var unixConnections = new UnixConnections();
        ConfigureListener(builder, settings, unixConnections);
        configure?.Invoke(builder);
        // One clock for the host (tests register a fake one first): the console's throttle and cache, the system-log
        // window and the connector's expiry checks all read it.
        builder.Services.TryAddSingleton(TimeProvider.System);

        var pin = new PinState();
        var handler = unifi ?? CertPin.CreateHandler(settings.CertPins, settings.Trust, pin);
        var catalog = OpenApiCatalog.Load();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(new UnifiClient(settings, handler, pin));
        builder.Services.AddSingleton(sp => new ConsoleReads(sp.GetRequiredService<UnifiClient>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(catalog);
        // Reads share one tool; each write is its own destructive tool, listed only when writes are on. Registered as
        // services (which the MCP server collects) so they get the host's client.
        var writes = catalog.Operations.Where(o => o.IsWrite && settings.AllowWrites).ToList();
        builder.Services.AddSingleton<McpServerTool>(sp => new ReadOperationTool(catalog, sp.GetRequiredService<UnifiClient>()));
        foreach (var op in writes)
            builder.Services.AddSingleton<McpServerTool>(sp => new ApiTool(op, sp.GetRequiredService<UnifiClient>()));
        // The Google callback URL carries the authorization code and state in its query. Hosting logs every request URL at
        // Information and OIDC traces the message at Trace, so neither may reach the logs.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Information);
        // The default scheme stays UnifiCaller: the sign-in cookie never authenticates /mcp. Only the tailnet listener reaches
        // the Tailscale handler, and the LAN listener only its own token (with or without a connector). The public side gets
        // the configured access token (MCP_ACCESS_TOKEN, when presented) or the bearer scheme, or (connector not configured)
        // a scheme that only refuses.
        var connector = ConnectorHosting.AddConnector(builder, settings, google);
        SetupPage.Add(builder, settings);
        var configured = connector.Configured;
        var bearerScheme = configured ? McpAuthenticationDefaults.AuthenticationScheme : PlaceholderBearerHandler.SchemeName;
        builder.Services.AddAuthentication(UnifiCallerScheme)
            .AddPolicyScheme(UnifiCallerScheme, null, o => o.ForwardDefaultSelector = ctx => ctx.ListenerOf() switch
            {
                Listener.Tailnet => TailscaleServeAuthHandler.SchemeName,
                Listener.Lan => AccessTokenHandler.SchemeName,
                _ => configured && AccessTokenHandler.Presented(ctx.Request, settings.AccessToken) ? AccessTokenHandler.SchemeName : bearerScheme,
            })
            .AddScheme<AuthenticationSchemeOptions, TailscaleServeAuthHandler>(TailscaleServeAuthHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, AccessTokenHandler>(AccessTokenHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, PlaceholderBearerHandler>(PlaceholderBearerHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy("unifi", p => p.AddAuthenticationSchemes(UnifiCallerScheme).RequireAuthenticatedUser()));
        var publicBase = settings.PublicBaseUrl?.GetLeftPart(UriPartial.Authority);
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new()
                {
                    Name = "unifi-network",
                    Title = "UniFi Network",
                    Description = "The UniFi Network API (v11.0.97) for this home network: read-only unless the owner enables writes.",
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                    // What Claude shows in its connector list. The icons are served on the public listener (Task 8).
                    WebsiteUrl = publicBase,
                    Icons = publicBase is null ? null :
                    [
                        new() { Source = $"{publicBase}/mcp/icon-512.png", MimeType = "image/png", Sizes = ["512x512"] },
                        new() { Source = $"{publicBase}/mcp/icon-180.png", MimeType = "image/png", Sizes = ["180x180"] },
                        new() { Source = $"{publicBase}/mcp/icon-32.png", MimeType = "image/png", Sizes = ["32x32"] },
                    ],
                };
                o.ServerInstructions = Instructions;
            })
            .WithHttpTransport(o => o.Stateless = true)
            // Who ran which tool, for every tool; arguments are never logged.
            .WithRequestFilters(f => f.AddCallToolFilter(Audit.Calls))
            .WithTools<GuideTools>()
            .WithTools<FaqTools>()
            .WithTools<DiagnosticTools>();

        var app = builder.Build();
        var publicOrigin = settings.PublicBaseUrl?.GetLeftPart(UriPartial.Authority);
        app.Use(async (ctx, next) =>
        {
            Listeners.Classify(ctx, settings, listenerOf, unixConnections);
            await next();
        });
        app.UseMiddleware<PublicGate>();
        app.Use(async (ctx, next) =>
        {
            if (RequestGuard.Check(ctx.ListenerOf(), ctx.Request.Path.Value ?? "/", ctx.Request.Method,
                    ctx.Request.Headers.Origin.ToString(), ctx.Request.ContentType, publicOrigin, $"{ctx.Request.Scheme}://{ctx.Request.Host}") is { } status)
            {
                ctx.Response.StatusCode = status;
                return;
            }
            await next();
        });
        ConnectorHosting.UseConnector(app);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/healthz", () => "ok").AllowAnonymous();
        app.MapMcp("/mcp").RequireAuthorization("unifi");
        ConnectorHosting.MapConnector(app);
        SetupPage.Map(app);

        var total = 1 + writes.Count + CountTools(typeof(GuideTools)) + CountTools(typeof(FaqTools)) + CountTools(typeof(DiagnosticTools));
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            RestrictSocket(settings);
            app.Logger.LogInformation("{Summary}", StartupSummary(settings, total, connector));
            if (settings.SetupPage)
                app.Logger.LogWarning("Setup page: {Link} (opens the sign-in code and checks; the key changes on each start)",
                    SetupPage.Link(settings, app.Services.GetRequiredService<SetupKey>()));
            if (settings.SetupPage && Listeners.SetupEndpoint(settings.SetupListen) is ({ } address, _) && !System.Net.IPAddress.IsLoopback(address)
                && Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true")
                app.Logger.LogWarning("The setup page listens on {Listen}, beyond this machine, over plain HTTP: anyone on the network who sees its traffic sees the sign-in code. Prefer 127.0.0.1.", settings.SetupListen);
            if (settings.LanListen is { } lan)
                app.Logger.LogWarning("The LAN listener on {Listen} is plain HTTP: anyone on the network who sees its traffic sees MCP_LAN_ACCESS_TOKEN, and the token gives full access to the tools. Use it only on a network you trust, or put a TLS proxy in front.", lan);
            if (Listeners.UnixPath(settings.PublicListen) is { } pub && Listeners.UnixPath(settings.Listen) is not null && !SocketPaths(settings).Contains(pub))
                app.Logger.LogWarning("Public socket not bound: the folder of {Socket} does not exist.", pub);
            // With the LAN listener on, an empty allow-list is the normal state: only the tailnet listener uses it.
            if (settings.AllowedLogins().Count == 0 && settings.LanListen is null)
                app.Logger.LogWarning("No allowed logins yet ({File}): every caller is refused.", settings.AllowedLoginsFile);
            else if (settings.AllowedLogins().Count == 0)
                app.Logger.LogInformation("No allowed logins ({File}): callers on {Listen} are refused; the LAN listener takes its token instead.",
                    settings.AllowedLoginsFile, settings.Listen);
        });
        return app;
    }

    /// <summary>One line for the logs: what is missing and what mode this is. Paths and counts only, never a value.</summary>
    /// <param name="connectorState">The host's connector state, so a broken keyring is reported; null judges by the settings alone.</param>
    public static string StartupSummary(Settings settings, int tools, ConnectorState? connectorState = null)
    {
        var missing = settings.Missing();
        var state = missing.Count == 0 ? "configured" : "not configured (" + string.Join("; ", missing) + ")";
        var publicMissing = settings.PublicMissing();
        // Broken reasons (e.g. "UNIFI_MCP_DATA_KEY does not match <dir>") name a path, never a value.
        var connector = publicMissing.Count > 0 ? "not configured (" + string.Join("; ", publicMissing) + ")"
            : connectorState?.Reason is { } reason ? "unavailable (" + reason + ")"
            : "ready";
        return $"unifi-mcp on {settings.Listen} for {settings.BaseUrl}: {state}, writes {(settings.AllowWrites ? "ON" : "off")}, {tools} tools, {settings.AllowedLogins().Count} allowed login(s), connector {connector}, sign-in {settings.SignIn.ToString().ToLowerInvariant()}, access token {(settings.AccessToken is null ? "off" : "on")}, LAN {settings.LanListen ?? "off"}";
    }

    private const string UnifiCallerScheme = "UnifiCaller";

    /// <summary>Both sockets in production. A TCP Listen is the development setup: only URLs are bound then, never a unix socket.</summary>
    private static void ConfigureListener(WebApplicationBuilder builder, Settings settings, UnixConnections unixConnections)
    {
        if (Listeners.UnixPath(settings.Listen) is null)
        {
            var urls = Listeners.UnixPath(settings.PublicListen) is null ? $"{settings.Listen};{settings.PublicListen}" : settings.Listen;
            if (settings.SetupPage) urls += $";{settings.SetupListen}";
            if (settings.LanListen is { } lanUrl) urls += $";{lanUrl}";
            builder.WebHost.UseUrls(urls);
            return;
        }
        var paths = SocketPaths(settings);
        foreach (var path in paths)
            if (File.Exists(path)) File.Delete(path); // stale socket from a previous run
        builder.WebHost.ConfigureKestrel(k =>
        {
            foreach (var path in paths) unixConnections.Listen(k, path);
            if (settings.SetupPage && Listeners.SetupEndpoint(settings.SetupListen) is var (address, port))
            {
                if (address is null) k.ListenLocalhost(port);
                else k.Listen(address, port);
            }
            if (settings.LanListen is { } lan && Listeners.SetupEndpoint(lan) is var (lanAddress, lanPort))
            {
                if (lanAddress is null) k.ListenLocalhost(lanPort);
                else k.Listen(lanAddress, lanPort);
            }
        });
    }

    /// <summary>The sockets to bind. The public one is skipped when its folder is missing, so the tailnet side still starts.</summary>
    private static List<string> SocketPaths(Settings settings) =>
        new[] { Listeners.UnixPath(settings.Listen), Listeners.UnixPath(settings.PublicListen) }
            .OfType<string>().Distinct()
            .Where(p => p == Listeners.UnixPath(settings.Listen) || Directory.Exists(Path.GetDirectoryName(p)))
            .ToList();

    /// <summary>Only the server's own user and root may connect to either socket.</summary>
    private static void RestrictSocket(Settings settings)
    {
        if (OperatingSystem.IsWindows() || Listeners.UnixPath(settings.Listen) is null) return;
        foreach (var path in SocketPaths(settings))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static int CountTools(Type type) =>
        type.GetMethods().Count(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
