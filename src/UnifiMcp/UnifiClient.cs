using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace UnifiMcp;

/// <summary>The call could not be made or the console refused it. The message is safe to show the caller.</summary>
public sealed class UnifiError(string message) : Exception(message);

/// <summary>The owner has not supplied the API key or certificate pin yet.</summary>
public sealed class UnifiNotConfigured(string message) : Exception(message);

public sealed record UnifiResponse(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>The fingerprint of the last certificate refused for not matching the pin, for a useful error.</summary>
public sealed class PinState
{
    public string? Rejected { get; set; }
}

/// <summary>
/// TLS to the console. Its certificate is self-signed, so it is pinned by SHA-256 of the DER instead of trusted by CA.
/// With no pin nothing matches, so verification cannot be off. Redirects are never followed, so the key cannot be sent elsewhere.
/// </summary>
public static class CertPin
{
    public static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    public static bool Matches(X509Certificate? cert, string? pin) =>
        cert is not null && !string.IsNullOrEmpty(pin) && string.Equals(Fingerprint(cert), pin, StringComparison.OrdinalIgnoreCase);

    public static SocketsHttpHandler CreateHandler(Func<string?> pin, PinState state) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (Matches(cert, pin())) return true;
                if (cert is not null) state.Rejected = Fingerprint(cert);
                return false;
            },
        },
    };
}

/// <summary>
/// The only code that talks to the console. Nothing is sent until the key and pin exist; non-GET requests are refused
/// here as well as by tool registration while writes are off; the key is scrubbed from everything returned.
/// The console's internal API (the one its own UI uses) is reached only through <see cref="ConsoleAsync"/>: a fixed list
/// of reads, cached briefly, sent one at a time and throttled, because that API was not built for outside polling.
/// </summary>
public sealed class UnifiClient(Settings settings, HttpMessageHandler handler, PinState pin, TimeProvider? time = null)
{
    private const string ApiRoot = "/proxy/network/integration";
    private const string ConsoleRoot = "/proxy/network";
    private const int PageSize = 200;

    /// <summary>Internal-API budget: a burst, then a steady rate. Generous for people, a wall for loops.</summary>
    public const int ConsoleBurst = 20;
    public const int ConsolePerMinute = 30;
    public static readonly TimeSpan ConsoleCacheFor = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<string, string> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _siteRefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _consoleGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, UnifiResponse Response)> _consoleCache = new(StringComparer.Ordinal);
    private double _tokens = ConsoleBurst;
    private DateTimeOffset _refilled = DateTimeOffset.MinValue;

    public Settings Settings => settings;

    public Task<UnifiResponse> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (method != HttpMethod.Get && !settings.AllowWrites)
            throw new UnifiError("Writes are disabled (UNIFI_ALLOW_WRITES is not true), so nothing was sent.");
        return SendCoreAsync(method, ApiRoot + path, body, ct);
    }

    /// <summary>
    /// The read-only internal endpoints the diagnostics tools use, relative to /proxy/network. POST is allowed only where
    /// the console takes a query in the body (the system log); everything else is a GET. Anything off this list is refused
    /// before a request exists, whatever UNIFI_ALLOW_WRITES says, so no tool can reach the console's command endpoints.
    /// </summary>
    private static readonly (string Method, System.Text.RegularExpressions.Regex Path)[] ConsoleReads =
    [
        ("GET", new(@"^/api/s/[A-Za-z0-9_-]+/(stat/(health|sysinfo|device|sta|rogueap)|rest/portforward)(\?[A-Za-z0-9=&_.-]*)?$")),
        ("GET", new(@"^/v2/api/site/[A-Za-z0-9_-]+/(aggregated-dashboard|clients/(active|history)|wan/load-balancing/status)(\?[A-Za-z0-9=&_.-]*)?$")),
        ("POST", new(@"^/v2/api/site/[A-Za-z0-9_-]+/system-log/(all|count)$")),
    ];

    public async Task<UnifiResponse> ConsoleAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (!ConsoleReads.Any(r => r.Method == method.Method && r.Path.IsMatch(path)))
            throw new UnifiError($"{method} {path.Split('?')[0]} is not one of the console reads this server allows, so nothing was sent.");
        if (settings.Missing().Count > 0) return await SendCoreAsync(method, ConsoleRoot + path, body, ct); // throws "not configured"

        var cacheKey = $"{method} {path}\n{body}";
        await _consoleGate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (_consoleCache.TryGetValue(cacheKey, out var hit) && now - hit.At < ConsoleCacheFor) return hit.Response;
            foreach (var stale in _consoleCache.Where(c => now - c.Value.At >= ConsoleCacheFor).Select(c => c.Key).ToList()) _consoleCache.Remove(stale);

            if (_refilled != DateTimeOffset.MinValue)
                _tokens = Math.Min(ConsoleBurst, _tokens + (now - _refilled).TotalSeconds * ConsolePerMinute / 60.0);
            _refilled = now;
            if (_tokens < 1)
            {
                var wait = Math.Ceiling((1 - _tokens) * 60.0 / ConsolePerMinute);
                throw new UnifiError($"Slow down: the console's internal API is limited to {ConsolePerMinute} calls a minute (bursts of {ConsoleBurst}) so polling cannot destabilise the network; try again in {wait:0} s.");
            }
            _tokens -= 1;

            var response = await SendCoreAsync(method, ConsoleRoot + path, body, ct);
            if (response.Ok) _consoleCache[cacheKey] = (now, response);
            return response;
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    /// <summary>An internal-API answer as JSON; non-2xx and a <c>meta.rc</c> other than ok become a <see cref="UnifiError"/>.</summary>
    public async Task<JsonElement> ConsoleJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var r = await ConsoleAsync(method, path, body is null ? null : JsonSerializer.Serialize(body), ct);
        var name = $"{method} {path.Split('?')[0]}";
        if (!r.Ok) throw new UnifiError($"The console answered {r.Status} to {name}: {Short(r.Body)}");
        JsonElement json;
        try { json = JsonDocument.Parse(r.Body).RootElement; }
        catch (JsonException) { throw new UnifiError($"The console's answer to {name} was not JSON."); }
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("meta", out var meta) && Str(meta, "rc") is { } rc && rc != "ok")
            throw new UnifiError($"The console refused {name}: {Str(meta, "msg") ?? rc}.");
        return json;
    }

    /// <summary>The <c>data</c> array of a classic internal-API answer (<c>{"meta":…,"data":[…]}</c>).</summary>
    public async Task<List<JsonElement>> ConsoleDataAsync(string path, CancellationToken ct)
    {
        var json = await ConsoleJsonAsync(HttpMethod.Get, path, null, ct);
        if (json.ValueKind == JsonValueKind.Array) return json.EnumerateArray().ToList();
        if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) return data.EnumerateArray().ToList();
        throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not the list this server expects.");
    }

    /// <summary>
    /// The internal API names a site by its internal reference ("default"), not its UUID. Takes a UUID, name or reference;
    /// omitted means the configured site.
    /// </summary>
    public async Task<string> ResolveSiteReferenceAsync(string? hint, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(hint) ? settings.Site : hint.Trim();
        if (_siteRefs.TryGetValue(wanted, out var known)) return known;
        var sites = await GetAllAsync("/v1/sites", ct);
        foreach (var s in sites)
        {
            if (new[] { Str(s, "id"), Str(s, "name"), Str(s, "internalReference") }.Any(v => string.Equals(v, wanted, StringComparison.OrdinalIgnoreCase))
                && Str(s, "internalReference") is { Length: > 0 } reference
                && System.Text.RegularExpressions.Regex.IsMatch(reference, "^[A-Za-z0-9_-]+$"))
                return _siteRefs[wanted] = reference;
        }
        throw new UnifiError($"The console has no site '{wanted}'. Sites: {string.Join(", ", sites.Select(s => Str(s, "name")))}.");
    }

    private (TimeZoneInfo Zone, string Name)? _zone;

    /// <summary>The console's own time zone (from stat/sysinfo, only that field), so times read as they do in its UI. UTC if unknown.</summary>
    public async Task<(TimeZoneInfo Zone, string Name)> ConsoleTimeZoneAsync(string siteRef, CancellationToken ct)
    {
        if (_zone is { } known) return known;
        try
        {
            var info = await ConsoleDataAsync($"/api/s/{siteRef}/stat/sysinfo", ct);
            if (info.Count > 0 && Str(info[0], "timezone") is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
                return (_zone = (zone, id)).Value;
        }
        catch (UnifiError)
        {
            // Times fall back to UTC; the tool's answer says which.
        }
        return (TimeZoneInfo.Utc, "UTC");
    }

    private async Task<UnifiResponse> SendCoreAsync(HttpMethod method, string rootedPath, string? body, CancellationToken ct)
    {
        var missing = settings.Missing();
        if (missing.Count > 0)
            throw new UnifiNotConfigured("UniFi MCP is not configured, so nothing was sent. Still needed: " + string.Join("; ", missing) + ".");

        var key = settings.ApiKey()!;
        using var req = new HttpRequestMessage(method, settings.BaseUrl.GetLeftPart(UriPartial.Authority) + rootedPath);
        req.Headers.Add("X-API-Key", key);
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return new UnifiResponse((int)resp.StatusCode, Scrub(await resp.Content.ReadAsStringAsync(ct), key));
        }
        catch (HttpRequestException ex)
        {
            var seen = pin.Rejected;
            pin.Rejected = null;
            throw new UnifiError(seen is not null
                ? $"The console's certificate does not match the pin, so nothing was sent. It presented SHA-256 {seen}; compare that with the console, then set UNIFI_CERT_SHA256 to it and restart the server (for example in .env, or with fleet-key), or put it in {settings.CertPinFile} if that variable is unset."
                : Scrub($"Could not reach the console: {ex.Message}", key));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UnifiError("The console did not answer in time.");
        }
    }

    public async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, path, null, ct);
        if (!r.Ok) throw new UnifiError($"The console answered {r.Status} to GET {path.Split('?')[0]}: {Short(r.Body)}");
        try { return JsonDocument.Parse(r.Body).RootElement; }
        catch (JsonException) { throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not JSON."); }
    }

    /// <summary>Every item of a paged list.</summary>
    public async Task<List<JsonElement>> GetAllAsync(string path, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var sep = path.Contains('?') ? '&' : '?';
        for (var offset = 0; ;)
        {
            var page = await GetJsonAsync($"{path}{sep}offset={offset}&limit={PageSize}", ct);
            try
            {
                var data = page.GetProperty("data").EnumerateArray().ToList();
                all.AddRange(data);
                offset += data.Count;
                if (data.Count == 0 || offset >= page.GetProperty("totalCount").GetInt64()) return all;
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not the paged list this server expects.");
            }
        }
    }

    /// <summary>A site UUID as is; a name or internal reference (default: the configured site) looked up.</summary>
    public async Task<string> ResolveSiteAsync(string? hint, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(hint) ? settings.Site : hint.Trim();
        if (Guid.TryParse(wanted, out _)) return wanted;
        if (_sites.TryGetValue(wanted, out var known)) return known;

        var sites = await GetAllAsync("/v1/sites", ct);
        foreach (var s in sites)
        {
            if (string.Equals(Str(s, "name"), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Str(s, "internalReference"), wanted, StringComparison.OrdinalIgnoreCase))
                return _sites[wanted] = Str(s, "id")!;
        }
        throw new UnifiError($"The console has no site '{wanted}'. Sites: {string.Join(", ", sites.Select(s => Str(s, "name")))}.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Scrub(string text, string key) => text.Replace(key, "[redacted]", StringComparison.Ordinal);

    private static string Short(string text) => text.Length <= 500 ? text : text[..500] + "...";
}
