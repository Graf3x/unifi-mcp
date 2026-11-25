using System.Text.RegularExpressions;

namespace UnifiMcp;

/// <summary>How the owner signs in on the connector's browser pages (MCP_SIGNIN).</summary>
public enum SignInMode
{
    /// <summary>Google OIDC, against the allowed Google emails. The default.</summary>
    Google,
    /// <summary>A one-time code the server writes to its log: whoever can read the log is the owner.</summary>
    Code,
}

/// <summary>How the console's TLS certificate is checked (UNIFI_CERT_TRUST).</summary>
public enum CertTrust
{
    /// <summary>Against the pinned fingerprints in UNIFI_CERT_SHA256: the certificate's or its public key's. The default.</summary>
    Pin,
    /// <summary>The usual way: a chain to a CA the system trusts, issued for the host in UNIFI_URL.</summary>
    Ca,
}

/// <summary>
/// Everything the server needs to know. The API key and certificate pin come first from the environment
/// (UNIFI_API_KEY and UNIFI_CERT_SHA256, set from .env, fleet-key or any other source), else from owner-supplied files
/// in the bind-mounted /config folder, which are read on each use; allow-lists likewise come from the environment or files. A missing value means "not configured", never a guessed default:
/// there is no default pin and no way to turn certificate checks off (UNIFI_CERT_TRUST=ca still checks, against a CA).
/// </summary>
public sealed partial record Settings
{
    public Uri BaseUrl { get; init; } = new("https://192.168.1.1");
    public string Site { get; init; } = "default";
    public string ApiKeyFile { get; init; } = "/config/unifi-api-key";
    public string CertPinFile { get; init; } = "/config/cert-sha256";
    public string? CertSha256 { get; init; }
    public CertTrust Trust { get; init; } = CertTrust.Pin;
    public string AllowedLoginsFile { get; init; } = "/config/allowed-logins";
    public string? AllowedLoginsValue { get; init; }
    public bool AllowWrites { get; init; }
    /// <summary>
    /// UNIFI_ALLOW_CONFIRM_FALLBACK: a client that cannot show the owner a confirmation form may still write, with
    /// <c>confirm</c> set to the tool's own name. The model sets that argument, so on such a client the model approves its
    /// own writes. Off (the default), such a client is refused every write.
    /// </summary>
    public bool AllowConfirmFallback { get; init; }
    public string Listen { get; init; } = "unix:/run/unifi-mcp/mcp.sock";
    public bool AllowLoopbackTcp { get; init; }

    // The public (Claude connector) side. Env-first so a container needs no files for these.
    public string? ApiKeyValue { get; init; }
    public string PublicListen { get; init; } = "unix:/run/unifi-mcp/public.sock";
    public Uri? PublicBaseUrl { get; init; }
    public string? GoogleClientId { get; init; }
    public string? GoogleClientSecret { get; init; }
    public Uri GoogleAuthority { get; init; } = new("https://accounts.google.com"); // never from the environment
    public string AllowedGoogleEmailsFile { get; init; } = "/config/allowed-google-emails";
    public string? AllowedGoogleEmailsValue { get; init; }
    public string DataDir { get; init; } = "/data";
    public string? DataKey { get; init; }
    public SignInMode SignIn { get; init; } = SignInMode.Google;
    /// <summary>MCP_ACCESS_TOKEN: a fixed credential /mcp accepts on the public side next to OAuth tokens. Null means none.</summary>
    public string? AccessToken { get; init; }
    /// <summary>
    /// MCP_SETUP_PAGE: the setup page, on its own listener (<see cref="SetupListen"/>) and never on the public one. From the
    /// environment it is on unless MCP_SIGNIN=google with a public URL (guide 3); a Settings built in code has it off.
    /// </summary>
    public bool SetupPage { get; init; }
    /// <summary>MCP_SETUP_LISTEN: http://&lt;IP address or localhost&gt;:&lt;port&gt;. Plain TCP; the page asks for its own key.</summary>
    public string SetupListen { get; init; } = DefaultSetupListen;
    public const string DefaultSetupListen = "http://127.0.0.1:5198";
    /// <summary>
    /// MCP_LAN_LISTEN: an opt-in listener for MCP clients elsewhere on the local network, http://&lt;IP address&gt;:&lt;port&gt;.
    /// It serves /mcp and /healthz only, to callers that send <see cref="LanAccessToken"/>. Null (the default) means off.
    /// </summary>
    public string? LanListen { get; init; }
    /// <summary>MCP_LAN_ACCESS_TOKEN: the only credential the LAN listener accepts. Required when it is on.</summary>
    public string? LanAccessToken { get; init; }

    private const int MinDataKeyLength = 16;
    public const int MinAccessTokenLength = 32;

    [GeneratedRegex("^[0-9A-F]{64}$")]
    private static partial Regex Sha256Hex();

    public static Settings FromEnvironment(Func<string, string?> env)
    {
        var d = new Settings();
        var url = Blank(env("UNIFI_URL"));
        var parsed = d.BaseUrl;
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out parsed!) || parsed.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("UNIFI_URL must be an https:// URL.");
        Uri? publicBase = null;
        var publicUrl = Blank(env("MCP_PUBLIC_BASE_URL"));
        if (publicUrl is not null)
        {
            if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out publicBase) || publicBase.Scheme != Uri.UriSchemeHttps
                || publicBase.AbsolutePath != "/" || publicBase.Query.Length > 0)
                throw new ArgumentException("MCP_PUBLIC_BASE_URL must be an https:// origin.");
        }
        var signIn = Blank(env("MCP_SIGNIN"))?.ToLowerInvariant() switch
        {
            null or "google" => SignInMode.Google,
            "code" => SignInMode.Code,
            _ => throw new ArgumentException("MCP_SIGNIN must be google or code."),
        };
        var trust = Blank(env("UNIFI_CERT_TRUST"))?.ToLowerInvariant() switch
        {
            null or "pin" => CertTrust.Pin,
            "ca" => CertTrust.Ca,
            _ => throw new ArgumentException("UNIFI_CERT_TRUST must be pin or ca."),
        };
        var setupPage = Blank(env("MCP_SETUP_PAGE"))?.ToLowerInvariant() switch
        {
            null => signIn != SignInMode.Google || publicBase is null, // off by default only in the full (Google) setup
            "on" or "true" => true,
            "off" or "false" => false,
            _ => throw new ArgumentException("MCP_SETUP_PAGE must be on or off."),
        };
        var setupListen = Blank(env("MCP_SETUP_LISTEN")) ?? d.SetupListen;
        if (Listeners.SetupEndpoint(setupListen) is not { } setupEndpoint)
            throw new ArgumentException("MCP_SETUP_LISTEN must be http://<IP address or localhost>:<port>, for example http://127.0.0.1:5198.");
        var accessToken = Blank(env("MCP_ACCESS_TOKEN"));
        if (accessToken is { Length: < MinAccessTokenLength })
            throw new ArgumentException($"MCP_ACCESS_TOKEN must be at least {MinAccessTokenLength} characters.");
        var lanListen = Blank(env("MCP_LAN_LISTEN"));
        var lanToken = Blank(env("MCP_LAN_ACCESS_TOKEN"));
        int? lanPort = null;
        if (lanListen is not null)
        {
            if (Listeners.SetupEndpoint(lanListen) is not { } lanEndpoint)
                throw new ArgumentException("MCP_LAN_LISTEN must be http://<IP address>:<port>, for example http://0.0.0.0:7420.");
            lanPort = lanEndpoint.Port;
            // Fail closed: the LAN listener has no other way to tell callers apart.
            if (lanToken is null)
                throw new ArgumentException("MCP_LAN_LISTEN needs MCP_LAN_ACCESS_TOKEN, the token every LAN caller must send. Generate one with: openssl rand -hex 32");
        }
        if (lanToken is { Length: < MinAccessTokenLength })
            throw new ArgumentException($"MCP_LAN_ACCESS_TOKEN must be at least {MinAccessTokenLength} characters.");
        // The LAN token crosses the network in plain HTTP; it must not also open the internet-facing side.
        if (lanToken is not null && lanToken == accessToken)
            throw new ArgumentException("MCP_LAN_ACCESS_TOKEN must differ from MCP_ACCESS_TOKEN: it travels over plain HTTP, so it must not also open the public connector.");
        var built = d with
        {
            SetupPage = setupPage,
            SetupListen = setupListen,
            BaseUrl = parsed,
            SignIn = signIn,
            Trust = trust,
            AccessToken = accessToken,
            Site = Blank(env("UNIFI_SITE")) ?? d.Site,
            ApiKeyFile = Blank(env("UNIFI_API_KEY_FILE")) ?? d.ApiKeyFile,
            CertPinFile = Blank(env("UNIFI_CERT_SHA256_FILE")) ?? d.CertPinFile,
            CertSha256 = Blank(env("UNIFI_CERT_SHA256")),
            AllowedLoginsFile = Blank(env("MCP_ALLOWED_LOGINS_FILE")) ?? d.AllowedLoginsFile,
            AllowedLoginsValue = Blank(env("MCP_ALLOWED_LOGINS")),
            AllowWrites = env("UNIFI_ALLOW_WRITES") == "true", // only the literal: a typo must leave writes off
            AllowConfirmFallback = env("UNIFI_ALLOW_CONFIRM_FALLBACK") == "true", // likewise: a typo leaves it off
            Listen = Blank(env("MCP_LISTEN")) ?? d.Listen,
            AllowLoopbackTcp = env("MCP_ALLOW_LOOPBACK_TCP") == "true",
            ApiKeyValue = Blank(env("UNIFI_API_KEY")),
            PublicListen = Blank(env("MCP_PUBLIC_LISTEN")) ?? d.PublicListen,
            PublicBaseUrl = publicBase,
            GoogleClientId = Blank(env("GOOGLE_CLIENT_ID")),
            GoogleClientSecret = Blank(env("GOOGLE_CLIENT_SECRET")),
            AllowedGoogleEmailsFile = Blank(env("MCP_ALLOWED_GOOGLE_EMAILS_FILE")) ?? d.AllowedGoogleEmailsFile,
            AllowedGoogleEmailsValue = Blank(env("MCP_ALLOWED_GOOGLE_EMAILS")),
            DataDir = Blank(env("MCP_DATA_DIR")) ?? d.DataDir,
            DataKey = Blank(env("UNIFI_MCP_DATA_KEY")),
            LanListen = lanListen,
            LanAccessToken = lanListen is null ? null : lanToken,
        };
        if (built.SetupPage && Listeners.PortsOf(built.Listen).Concat(Listeners.PortsOf(built.PublicListen)).Contains(setupEndpoint.Port))
            throw new ArgumentException("MCP_SETUP_LISTEN needs a port of its own, not the one in MCP_LISTEN or MCP_PUBLIC_LISTEN.");
        if (lanPort is { } lp && (Listeners.PortsOf(built.Listen).Concat(Listeners.PortsOf(built.PublicListen)).Contains(lp)
                || (built.SetupPage && setupEndpoint.Port == lp)))
            throw new ArgumentException("MCP_LAN_LISTEN needs a port of its own, not the one in MCP_LISTEN, MCP_PUBLIC_LISTEN or MCP_SETUP_LISTEN.");
        return built;
    }

    /// <summary>Whether a pin was given at all, valid or not, so the setup page can tell a typo from nothing.</summary>
    public bool CertPinGiven() => (Blank(CertSha256) ?? ReadFile(CertPinFile)) is not null;

    /// <summary>The UniFi API key: the environment value if set, else the owner-supplied file.</summary>
    public string? ApiKey() => Blank(ApiKeyValue) ?? ReadFile(ApiKeyFile);

    /// <summary>
    /// The pinned SHA-256 fingerprints, each 64 upper-case hex digits: of the console's certificate or of its public key
    /// (SubjectPublicKeyInfo). Several may be listed, separated by commas or white space, so a new one can be added before
    /// the old one is removed. Null if unset, or if any entry is malformed (a typo must not quietly drop a pin).
    /// </summary>
    public IReadOnlyList<string>? CertPins()
    {
        var raw = Blank(CertSha256) ?? ReadFile(CertPinFile);
        if (raw is null) return null;
        var pins = raw.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant())
            .Distinct().ToList();
        return pins.Count > 0 && pins.All(p => Sha256Hex().IsMatch(p)) ? pins : null;
    }

    public IReadOnlyList<string> AllowedLogins() =>
        (Blank(AllowedLoginsValue) ?? ReadFile(AllowedLoginsFile) ?? "")
        .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> AllowedGoogleEmails() =>
        (Blank(AllowedGoogleEmailsValue) ?? ReadFile(AllowedGoogleEmailsFile) ?? "")
        .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>What the public (connector) side still needs. Names only, never values.</summary>
    public IReadOnlyList<string> PublicMissing()
    {
        var missing = new List<string>();
        if (PublicBaseUrl is null) missing.Add("MCP_PUBLIC_BASE_URL");
        var google = SignIn == SignInMode.Google;
        if (google && Blank(GoogleClientId) is null) missing.Add("GOOGLE_CLIENT_ID");
        if (google && Blank(GoogleClientSecret) is null) missing.Add("GOOGLE_CLIENT_SECRET");
        if ((Blank(DataKey)?.Length ?? 0) < MinDataKeyLength) missing.Add("UNIFI_MCP_DATA_KEY");
        if (google && AllowedGoogleEmails().Count == 0) missing.Add($"allowed Google emails (MCP_ALLOWED_GOOGLE_EMAILS or {AllowedGoogleEmailsFile})");
        return missing;
    }

    /// <summary>Record printing for logs: secret values are masked.</summary>
    private bool PrintMembers(System.Text.StringBuilder sb)
    {
        sb.Append($"BaseUrl = {BaseUrl}, Site = {Site}, ApiKeyFile = {ApiKeyFile}, CertPinFile = {CertPinFile}, ")
          .Append($"AllowedLoginsFile = {AllowedLoginsFile}, AllowWrites = {AllowWrites}, AllowConfirmFallback = {AllowConfirmFallback}, Listen = {Listen}, ")
          .Append($"AllowLoopbackTcp = {AllowLoopbackTcp}, ApiKeyValue = {Mask(ApiKeyValue)}, PublicListen = {PublicListen}, ")
          .Append($"PublicBaseUrl = {PublicBaseUrl}, GoogleClientId = {GoogleClientId}, GoogleClientSecret = {Mask(GoogleClientSecret)}, ")
          .Append($"GoogleAuthority = {GoogleAuthority}, AllowedGoogleEmailsFile = {AllowedGoogleEmailsFile}, ")
          .Append($"DataDir = {DataDir}, DataKey = {Mask(DataKey)}, SignIn = {SignIn}, AccessToken = {Mask(AccessToken)}, Trust = {Trust}, ")
          .Append($"SetupPage = {SetupPage}, SetupListen = {SetupListen}, LanListen = {LanListen ?? "(off)"}, LanAccessToken = {Mask(LanAccessToken)}");
        return true;
    }

    private static string Mask(string? secret) => secret is null ? "(unset)" : "***";

    /// <summary>What the owner still has to provide before the console can be called. Names paths, never values.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (ApiKey() is null) missing.Add($"API key: set UNIFI_API_KEY in the environment (for example in .env, or with fleet-key) to the UniFi Network API key, or put the key in {ApiKeyFile}");
        if (Trust == CertTrust.Pin && CertPins() is null)
            missing.Add($"certificate pin: set UNIFI_CERT_SHA256 in the environment to the SHA-256 fingerprint of the console's certificate or of its public key (64 hex digits; list several separated by commas), or put it in {CertPinFile}. If the console has a certificate from a trusted CA, set UNIFI_CERT_TRUST=ca instead");
        return missing;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? Blank(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
