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

/// <summary>
/// Everything the server needs to know. The API key and certificate pin come first from the environment
/// (UNIFI_API_KEY and UNIFI_CERT_SHA256, set from .env, fleet-key or any other source), else from owner-supplied files
/// in the bind-mounted /config folder, which are read on each use; allow-lists likewise come from the environment or files. A missing value means "not configured", never a guessed default:
/// there is no default pin and no way to turn certificate checks off.
/// </summary>
public sealed partial record Settings
{
    public Uri BaseUrl { get; init; } = new("https://192.168.1.1");
    public string Site { get; init; } = "default";
    public string ApiKeyFile { get; init; } = "/config/unifi-api-key";
    public string CertPinFile { get; init; } = "/config/cert-sha256";
    public string? CertSha256 { get; init; }
    public string AllowedLoginsFile { get; init; } = "/config/allowed-logins";
    public string? AllowedLoginsValue { get; init; }
    public bool AllowWrites { get; init; }
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
        var accessToken = Blank(env("MCP_ACCESS_TOKEN"));
        if (accessToken is { Length: < MinAccessTokenLength })
            throw new ArgumentException($"MCP_ACCESS_TOKEN must be at least {MinAccessTokenLength} characters.");
        return d with
        {
            BaseUrl = parsed,
            SignIn = signIn,
            AccessToken = accessToken,
            Site = Blank(env("UNIFI_SITE")) ?? d.Site,
            ApiKeyFile = Blank(env("UNIFI_API_KEY_FILE")) ?? d.ApiKeyFile,
            CertPinFile = Blank(env("UNIFI_CERT_SHA256_FILE")) ?? d.CertPinFile,
            CertSha256 = Blank(env("UNIFI_CERT_SHA256")),
            AllowedLoginsFile = Blank(env("MCP_ALLOWED_LOGINS_FILE")) ?? d.AllowedLoginsFile,
            AllowedLoginsValue = Blank(env("MCP_ALLOWED_LOGINS")),
            AllowWrites = env("UNIFI_ALLOW_WRITES") == "true", // only the literal: a typo must leave writes off
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
        };
    }

    /// <summary>The UniFi API key: the environment value if set, else the owner-supplied file.</summary>
    public string? ApiKey() => Blank(ApiKeyValue) ?? ReadFile(ApiKeyFile);

    /// <summary>The pinned SHA-256 of the console's certificate as 64 upper-case hex digits, or null if unset or malformed.</summary>
    public string? CertPin()
    {
        var raw = Blank(CertSha256) ?? ReadFile(CertPinFile);
        var hex = raw?.Replace(":", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return hex is not null && Sha256Hex().IsMatch(hex) ? hex : null;
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
          .Append($"AllowedLoginsFile = {AllowedLoginsFile}, AllowWrites = {AllowWrites}, Listen = {Listen}, ")
          .Append($"AllowLoopbackTcp = {AllowLoopbackTcp}, ApiKeyValue = {Mask(ApiKeyValue)}, PublicListen = {PublicListen}, ")
          .Append($"PublicBaseUrl = {PublicBaseUrl}, GoogleClientId = {GoogleClientId}, GoogleClientSecret = {Mask(GoogleClientSecret)}, ")
          .Append($"GoogleAuthority = {GoogleAuthority}, AllowedGoogleEmailsFile = {AllowedGoogleEmailsFile}, ")
          .Append($"DataDir = {DataDir}, DataKey = {Mask(DataKey)}, SignIn = {SignIn}, AccessToken = {Mask(AccessToken)}");
        return true;
    }

    private static string Mask(string? secret) => secret is null ? "(unset)" : "***";

    /// <summary>What the owner still has to provide before the console can be called. Names paths, never values.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (ApiKey() is null) missing.Add($"API key: set UNIFI_API_KEY in the environment (for example in .env, or with fleet-key) to the UniFi Network API key, or put the key in {ApiKeyFile}");
        if (CertPin() is null) missing.Add($"certificate pin: set UNIFI_CERT_SHA256 in the environment to the console's SHA-256 certificate fingerprint (64 hex digits), or put it in {CertPinFile}");
        return missing;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? Blank(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
