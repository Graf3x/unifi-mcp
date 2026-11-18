using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace UnifiMcp;

[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
public enum CheckStatus { Ok, Warn, Fail, Skip, Blocked }

/// <summary>A value the page offers to copy, such as a fingerprint to pin. Never a secret.</summary>
public sealed record CopyValue(string Label, string Value);

/// <summary>One check's answer. <see cref="Anchor"/> names the heading in the setup guide that fixes it.</summary>
public sealed record CheckResult(string Id, CheckStatus Status, string Summary, string? Detail = null, string? Anchor = null,
    IReadOnlyList<CopyValue>? Copy = null);

/// <summary>What the console presented in a TLS handshake: fingerprints and the platform's verdict, nothing else.</summary>
public sealed record ProbedCertificate(string Certificate, string Key, SslPolicyErrors Errors, long Millis);

/// <summary>The console could not be reached at all; the message says how, in words for the page.</summary>
public sealed class ProbeFailed(string message) : Exception(message);

/// <summary>Opens a TLS connection to the console to see its certificate. Replaced in tests.</summary>
public interface ICertificateProbe
{
    Task<ProbedCertificate> ProbeAsync(Uri console, CancellationToken ct);
}

/// <summary>
/// A TLS handshake and nothing more: the certificate is accepted only to be looked at, and the connection is closed
/// before a byte of HTTP (or the API key) is sent. The real calls go through <see cref="CertPin"/>, which checks it.
/// </summary>
public sealed class TlsCertificateProbe : ICertificateProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<ProbedCertificate> ProbeAsync(Uri console, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var watch = Stopwatch.StartNew();
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(console.Host, console.Port, cts.Token);
            X509Certificate2? seen = null;
            var errors = SslPolicyErrors.None;
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = console.Host,
                RemoteCertificateValidationCallback = (_, cert, _, e) =>
                {
                    if (cert is not null) seen = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    errors = e;
                    return true; // looked at, never trusted: nothing is sent on this connection
                },
            }, cts.Token);
            watch.Stop();
            if (seen is null) throw new ProbeFailed("The console finished the TLS handshake without a certificate.");
            using (seen) return new(CertPin.Fingerprint(seen), CertPin.KeyFingerprint(seen), errors, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProbeFailed($"No answer from {console.Host}:{console.Port} within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (SocketException e)
        {
            throw new ProbeFailed(e.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"The name {console.Host} doesn't resolve from where this server runs.",
                SocketError.ConnectionRefused => $"{console.Host} refused the connection on port {console.Port}: nothing listens there.",
                SocketError.TimedOut => $"No answer from {console.Host}:{console.Port}.",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => $"There is no route from this server to {console.Host}.",
                _ => $"Could not connect to {console.Host}:{console.Port} ({e.SocketErrorCode}).",
            });
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            throw new ProbeFailed($"{console.Host}:{console.Port} answers, but not with TLS: {e.Message}");
        }
        catch (Exception e) when (e is not (ProbeFailed or OperationCanceledException))
        {
            // The platform's TLS stack can fail in its own ways (Windows throws an internal exception for some
            // handshakes): a failed check, not a failed page.
            throw new ProbeFailed($"The TLS handshake with {console.Host}:{console.Port} failed: {e.Message}");
        }
    }
}

/// <summary>
/// The setup page's checks: each one runs on its own, so the page can light them up as they finish. They only read: a
/// TLS handshake, the API's /v1/info and site list, and the public /healthz through the tunnel. No answer carries a
/// secret; the API key, tokens and keys are reported as set or not set.
/// </summary>
public sealed class SetupChecks(Settings settings, UnifiClient client, ConnectorState connector, ICertificateProbe probe,
    [FromKeyedServices(SetupChecks.OutboundKey)] HttpMessageHandler outbound)
{
    public const string OutboundKey = "setup-outbound";
    public static readonly TimeSpan TunnelTimeout = TimeSpan.FromSeconds(10);
    public static readonly IReadOnlyList<string> Ids = ["console", "certificate", "apikey", "site", "connector", "tunnel", "signin", "callers", "lan"];

    // Guide headings the fixes point at (GitHub's ids for the README headings).
    private const string BeforeYouStart = "before-you-start-two-values-from-your-console";
    private const string Guide1 = "guide-1-quick-local-setup";
    private const string ConsoleValues = "step-2-console-values";
    private const string Tunnel = "step-3-cloudflare-tunnel";
    private const string RestOfEnv = "step-4-the-rest-of-env-then-start";
    private const string GoogleClient = "step-1-a-google-oauth-client";
    private const string Tailscale = "step-4-optional-claude-code-over-tailscale";
    private const string Guide4 = "guide-4-claude-code-across-your-lan";

    private readonly HttpClient _outbound = new(outbound, disposeHandler: false) { Timeout = TunnelTimeout };

    private int Guide => SetupGuide.GuideOf(settings);
    private string ConsoleAnchor => Guide == 1 ? Guide1 : ConsoleValues;

    public Task<CheckResult> RunAsync(string id, CancellationToken ct) => id switch
    {
        "console" => ConsoleAsync(ct),
        "certificate" => CertificateAsync(ct),
        "apikey" => ApiKeyAsync(ct),
        "site" => SiteAsync(ct),
        "connector" => Task.FromResult(Connector()),
        "tunnel" => TunnelAsync(ct),
        "signin" => Task.FromResult(SignIn()),
        "callers" => Task.FromResult(Callers()),
        "lan" => Task.FromResult(Lan()),
        _ => throw new KeyNotFoundException(id),
    };

    private async Task<CheckResult> ConsoleAsync(CancellationToken ct)
    {
        try
        {
            var seen = await probe.ProbeAsync(settings.BaseUrl, ct);
            return new("console", CheckStatus.Ok, $"Answers at {settings.BaseUrl.Host}:{settings.BaseUrl.Port} in {seen.Millis} ms.");
        }
        catch (ProbeFailed e)
        {
            return new("console", CheckStatus.Fail, e.Message,
                "UNIFI_URL must be the console's address as this server reaches it. In Docker, that is from inside the container.",
                ConsoleAnchor);
        }
    }

    private async Task<CheckResult> CertificateAsync(CancellationToken ct)
    {
        ProbedCertificate seen;
        try { seen = await probe.ProbeAsync(settings.BaseUrl, ct); }
        catch (ProbeFailed) { return new("certificate", CheckStatus.Blocked, "Waiting for the console to answer."); }

        CopyValue[] both = [new("Public key SHA-256", seen.Key), new("Certificate SHA-256", seen.Certificate)];
        if (settings.Trust == CertTrust.Ca)
        {
            return seen.Errors == SslPolicyErrors.None
                ? new("certificate", CheckStatus.Ok, $"Trusted: a valid chain to a CA this server trusts, issued for {settings.BaseUrl.Host}.",
                    "UNIFI_CERT_TRUST=ca, so renewals need nothing from you.")
                : new("certificate", CheckStatus.Fail, $"Not trusted ({seen.Errors}).",
                    $"With UNIFI_CERT_TRUST=ca the certificate needs a valid chain to a CA this server trusts, issued for {settings.BaseUrl.Host} (the host in UNIFI_URL). If the console keeps its own self-signed certificate, set UNIFI_CERT_TRUST=pin and pin its public key instead.",
                    BeforeYouStart, both);
        }

        var pins = settings.CertPins();
        if (pins is null)
        {
            return settings.CertPinGiven()
                ? new("certificate", CheckStatus.Fail, "UNIFI_CERT_SHA256 is set, but one of its entries isn't a fingerprint.",
                    "Each entry is 64 hex digits (colons are fine), and several are separated by commas. Here is what the console presents now.",
                    BeforeYouStart, both)
                : new("certificate", CheckStatus.Fail, "No pin yet.",
                    "Below is what the console presents to this server. Before you pin it, confirm it on the console itself (over SSH, as the guide shows), so you are not pinning something in the middle. Then set UNIFI_CERT_SHA256 to the public key's fingerprint and restart the server.",
                    BeforeYouStart, both);
        }
        if (pins.Contains(seen.Key, StringComparer.OrdinalIgnoreCase))
            return new("certificate", CheckStatus.Ok, "Matches your pin of the console's public key.");
        if (pins.Contains(seen.Certificate, StringComparer.OrdinalIgnoreCase))
            return new("certificate", CheckStatus.Ok, "Matches your pin of the console's certificate.",
                "Pinning the public key instead keeps working when the console re-issues its certificate with the same key. Both can be listed, comma separated.",
                BeforeYouStart, [both[0]]);
        return new("certificate", CheckStatus.Fail, "Doesn't match your pin.",
            "The console now presents the fingerprints below. A firmware update or restart can give it a new certificate. Confirm one on the console, then add it to UNIFI_CERT_SHA256 (the old and new can sit side by side, comma separated) and restart the server.",
            BeforeYouStart, both);
    }

    private async Task<CheckResult> ApiKeyAsync(CancellationToken ct)
    {
        if (settings.ApiKey() is null)
            return new("apikey", CheckStatus.Fail, "No API key yet.",
                "Create one in UniFi Network under Settings, Control Plane, Integrations, and set UNIFI_API_KEY to it.", BeforeYouStart);
        if (settings.Missing().Count > 0)
            return new("apikey", CheckStatus.Blocked, "Waiting for a certificate pin.", "The key is never sent to a console whose certificate hasn't been checked.");
        try
        {
            var r = await client.SendAsync(HttpMethod.Get, "/v1/info", null, ct);
            if (r.Status is 401 or 403)
                return new("apikey", CheckStatus.Fail, $"The console refused the key ({r.Status}).",
                    "It may have been deleted or mistyped. Create a new one under Settings, Control Plane, Integrations, set UNIFI_API_KEY to it and restart the server.",
                    BeforeYouStart);
            if (!r.Ok)
                return new("apikey", CheckStatus.Fail, $"The console answered {r.Status} to /v1/info.",
                    "UNIFI_URL may point at something other than a UniFi console, or its Network application isn't running.", ConsoleAnchor);
            var version = System.Text.Json.JsonDocument.Parse(r.Body).RootElement.TryGetProperty("applicationVersion", out var v) ? v.GetString() : null;
            return new("apikey", CheckStatus.Ok, version is null ? "Accepted." : $"Accepted by UniFi Network {version}.");
        }
        catch (UnifiError e) when (IsCertificate(e))
        {
            return new("apikey", CheckStatus.Blocked, "Waiting on the certificate check.");
        }
        catch (Exception e) when (e is UnifiError or System.Text.Json.JsonException)
        {
            return new("apikey", CheckStatus.Fail, e is UnifiError ? e.Message : "The console's answer to /v1/info was not JSON.", null, ConsoleAnchor);
        }
    }

    private async Task<CheckResult> SiteAsync(CancellationToken ct)
    {
        if (settings.Missing().Count > 0) return new("site", CheckStatus.Blocked, "Waiting for the API key and pin.");
        try
        {
            await client.ResolveSiteAsync(null, ct);
            return new("site", CheckStatus.Ok, $"Found the site '{settings.Site}'.");
        }
        catch (UnifiError e) when (IsCertificate(e) || e.Message.Contains("answered 401", StringComparison.Ordinal) || e.Message.Contains("answered 403", StringComparison.Ordinal))
        {
            return new("site", CheckStatus.Blocked, "Waiting on the API key.");
        }
        catch (UnifiError e)
        {
            return new("site", CheckStatus.Fail, e.Message, "UNIFI_SITE takes a site's name as UniFi shows it, or its id. Most consoles have one site, called default.", ConsoleAnchor);
        }
    }

    private CheckResult Connector()
    {
        if (settings.PublicBaseUrl is null && Guide is 1 or 4)
            return new("connector", CheckStatus.Skip, Guide == 1 ? "Not used: Claude Code connects on this machine." : "Not used: Claude Code connects over the LAN listener.",
                "Guides 2 and 3 add a connector for claude.ai and the apps.");
        var missing = settings.PublicMissing();
        if (missing.Count > 0)
            return new("connector", CheckStatus.Fail, "Not configured.", "Still needed: " + string.Join("; ", missing) + ".", RestOfEnv);
        if (connector.Reason is { } reason)
            return new("connector", CheckStatus.Fail, $"Unavailable: {reason}.",
                "If UNIFI_MCP_DATA_KEY changed, put the old value back, or stop the server and delete ./data to start over (every client then connects again).", RestOfEnv);
        return new("connector", CheckStatus.Ok, $"Ready at {settings.PublicBaseUrl!.GetLeftPart(UriPartial.Authority)}/mcp.");
    }

    private async Task<CheckResult> TunnelAsync(CancellationToken ct)
    {
        if (settings.PublicBaseUrl is not { } pub)
            return Guide is 1 or 4
                ? new("tunnel", CheckStatus.Skip, "Not used: nothing is reachable from the internet.")
                : new("tunnel", CheckStatus.Fail, "MCP_PUBLIC_BASE_URL isn't set, so there is nothing to test.", "Set it to the tunnel's public hostname, for example https://unifi.example.com.", RestOfEnv);

        var url = new Uri(pub, "/healthz");
        HttpResponseMessage r;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            r = await _outbound.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new("tunnel", CheckStatus.Fail, $"No answer from {pub.Host} within {TunnelTimeout.TotalSeconds:0} seconds.", null, Tunnel);
        }
        catch (HttpRequestException e)
        {
            return new("tunnel", CheckStatus.Fail, $"Couldn't reach {pub.Host} from this server.",
                $"{e.Message} Check that the hostname exists in Cloudflare's DNS and that this server can reach the internet.", Tunnel);
        }

        using (r)
        {
            var status = (int)r.StatusCode;
            var body = await r.Content.ReadAsStringAsync(ct);
            var host = pub.Host;
            if (status == 200 && body.Trim() == "ok")
                return new("tunnel", CheckStatus.Ok, $"Cloudflare forwards {host} to this server.");
            if (status == 503 && body.Contains("isn't set up", StringComparison.Ordinal))
                return new("tunnel", CheckStatus.Ok, $"Cloudflare forwards {host} to this server.", "The connector answers 503 until it's ready; see Connector.");
            if (status is >= 300 and < 400 && r.Headers.Location is { } to && to.IsAbsoluteUri && to.Host.EndsWith("cloudflareaccess.com", StringComparison.OrdinalIgnoreCase))
                return new("tunnel", CheckStatus.Fail, "Cloudflare Access asks for a login on this hostname.",
                    "Claude's servers can't pass it. Remove the Access application that covers it (watch for a wildcard such as *.example.com).", Tunnel);
            if (status == 403 && r.Headers.Contains("cf-mitigated"))
                return new("tunnel", CheckStatus.Fail, "A Cloudflare challenge blocks this hostname.",
                    "Claude's servers can't pass a challenge. Turn off the WAF, bot-fight or challenge rule for it.", Tunnel);
            if (status == 421)
                return new("tunnel", CheckStatus.Fail, "The request reached this server under another host name.",
                    "MCP_PUBLIC_BASE_URL must match the tunnel's public hostname exactly.", Tunnel);
            if (status == 530)
                return new("tunnel", CheckStatus.Fail, "Cloudflare can't reach the tunnel.",
                    "Is the cloudflared container running and connected? Run docker compose logs cloudflared. Outbound port 7844 must be open.", Tunnel);
            if (status is 502 or 504)
                return new("tunnel", CheckStatus.Fail, "The tunnel is up, but it can't reach this server.",
                    "The route's service must be unix:/run/unifi-mcp/public.sock, and ./run must be mounted in both containers.", Tunnel);
            if (status == 404)
                return new("tunnel", CheckStatus.Fail, $"{host} answered 404.",
                    "The tunnel may have no route for this hostname, or the route's path isn't empty.", Tunnel);
            return new("tunnel", CheckStatus.Fail, $"{host} answered {status}, not this server's health check.", null, Tunnel);
        }
    }

    private CheckResult SignIn()
    {
        if (settings.PublicBaseUrl is null && Guide is 1 or 4)
            return new("signin", CheckStatus.Skip, Guide == 1 ? "Not used: there is no sign-in page in guide 1." : "Not used: LAN callers send the access token instead.");
        var pub = settings.PublicBaseUrl?.GetLeftPart(UriPartial.Authority) ?? "https://unifi.example.com";
        if (settings.SignIn == SignInMode.Code)
        {
            return connector.Ready
                ? new("signin", CheckStatus.Ok, "One-time codes. When Claude opens the sign-in page, the code appears on this page.")
                : new("signin", CheckStatus.Blocked, "Waiting for the connector.");
        }

        var problems = new List<string>();
        if (settings.GoogleClientId is null) problems.Add("GOOGLE_CLIENT_ID isn't set");
        else if (!settings.GoogleClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            problems.Add("GOOGLE_CLIENT_ID doesn't look like a Google client ID (it ends in .apps.googleusercontent.com)");
        if (settings.GoogleClientSecret is null) problems.Add("GOOGLE_CLIENT_SECRET isn't set");
        var emails = settings.AllowedGoogleEmails().Count;
        if (emails == 0) problems.Add("MCP_ALLOWED_GOOGLE_EMAILS is empty, so nobody can sign in");
        CopyValue[] redirect = [new("Authorised redirect URI", pub + "/signin-google")];
        return problems.Count > 0
            ? new("signin", CheckStatus.Fail, "Google sign-in isn't complete.", string.Join(". ", problems) + ".", GoogleClient, redirect)
            : new("signin", CheckStatus.Ok, $"Google sign-in for {emails} address{(emails == 1 ? "" : "es")}.",
                "The Google OAuth client needs this redirect URI.", GoogleClient, redirect);
    }

    private CheckResult Callers()
    {
        var logins = settings.AllowedLogins();
        if (Guide == 1)
        {
            if (!settings.AllowLoopbackTcp)
                return new("callers", CheckStatus.Fail, "MCP_ALLOW_LOOPBACK_TCP isn't true.",
                    "Without it the server refuses Claude Code on 127.0.0.1. Set MCP_ALLOW_LOOPBACK_TCP=true and restart.", Guide1);
            return logins.Count == 0
                ? new("callers", CheckStatus.Fail, "MCP_ALLOWED_LOGINS is empty, so every caller is refused.", "Set it to a name, such as me, and send the same name in Claude Code's header.", Guide1)
                : new("callers", CheckStatus.Ok, $"Claude Code gets in with the header Tailscale-User-Login: {logins[0]}.", null, Guide1,
                    [new("Header", $"{CallerPolicy.LoginHeader}: {logins[0]}")]);
        }
        return logins.Count == 0
            ? new("callers", CheckStatus.Skip, "Optional, not set up.", "Guide 3, step 4, adds Claude Code over Tailscale.", Tailscale)
            : new("callers", CheckStatus.Ok, $"{logins.Count} Tailscale login{(logins.Count == 1 ? "" : "s")} allowed on mcp.sock.",
                "Publish it on the host with tailscale serve; the server can't see from here whether that is running.", Tailscale);
    }

    private CheckResult Lan()
    {
        if (settings.LanListen is not { } listen || Listeners.SetupEndpoint(listen) is not var (address, port))
            return new("lan", CheckStatus.Skip, "Not used: MCP_LAN_LISTEN isn't set.",
                "Guide 4 adds a token-only listener for Claude Code elsewhere on your network.", Guide4);
        CopyValue[] command = [new("Claude Code", LanCommand(settings))];
        if (address is null || IPAddress.IsLoopback(address))
            return new("lan", CheckStatus.Warn, $"Listening on {listen}, which only this machine can reach.",
                InContainer
                    ? "Inside a container nothing else reaches that address. Set MCP_LAN_LISTEN=http://0.0.0.0:" + port + " and publish the port."
                    : "To reach it from other machines, use this machine's LAN address or 0.0.0.0.",
                Guide4, command);
        return new("lan", CheckStatus.Ok, $"Token-only on port {port}; MCP_LAN_ACCESS_TOKEN is set.",
            "It is plain HTTP, so anyone who can see the network's traffic can read the token. Use the address of the machine that runs the server"
            + (InContainer ? " and the port you published" : "") + ", and the token from your settings: this page never shows it.",
            Guide4, command);
    }

    private static bool InContainer => Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    /// <summary>The LAN listener's MCP URL, with a placeholder for the address when it listens on every interface.</summary>
    public static string LanUrl(Settings s)
    {
        var (address, port) = s.LanListen is { } l && Listeners.SetupEndpoint(l) is { } e ? e : (null, 7420);
        var host = address is null ? "localhost"
            : address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? "<server-ip>"
            : address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"http://{host}:{port}/mcp";
    }

    /// <summary>The claude mcp add line for the LAN listener, with a placeholder for the token.</summary>
    public static string LanCommand(Settings s) =>
        $"claude mcp add --transport http unifi {LanUrl(s)} --header \"Authorization: Bearer <MCP_LAN_ACCESS_TOKEN>\"";

    /// <summary>UnifiClient's TLS refusals, which start this way (see its SendCoreAsync).</summary>
    private static bool IsCertificate(UnifiError e) => e.Message.StartsWith("The console's certificate", StringComparison.Ordinal);
}
