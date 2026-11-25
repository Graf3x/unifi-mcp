using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static UnifiMcp.ConsoleJson;

namespace UnifiMcp;

/// <summary>
/// A page of the console's system log over the last <paramref name="Hours"/>: the page, and the filters the console
/// applies. A filter left null is not sent. The window's times are set when the console is asked, so the same question
/// asked again within <see cref="ConsoleReads.CacheFor"/> is the same read.
/// </summary>
public sealed record SystemLogQuery(
    int Hours,
    int PageSize,
    int PageNumber,
    string[]? Categories,
    string[]? Events,
    string[]? Severities,
    string? SearchText);

/// <summary>The span a system-log answer covers: the last <paramref name="Hours"/> up to <paramref name="To"/> (Unix milliseconds).</summary>
public sealed record LogWindow(int Hours, long From, long To);

/// <summary>A page of the system log and the window the console was asked about.</summary>
public sealed record SystemLogPage(JsonElement Json, LogWindow Window);

/// <summary>
/// The console's internal API (the one its own UI uses), read through named reads for one site. That API was not built
/// for outside polling, so every read is cached briefly, sent one at a time and throttled, process-wide. Only these reads
/// exist: nothing here can reach the console's command endpoints, whatever UNIFI_ALLOW_WRITES says.
/// </summary>
public sealed partial class ConsoleReads(UnifiClient client, TimeProvider time)
{
    /// <summary>The budget: a burst, then a steady rate. Generous for people, a wall for loops.</summary>
    public const int Burst = 20;
    public const int PerMinute = 30;
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>An answer kept for <see cref="CacheFor"/>: when it was read by the steady clock, and by the wall clock (Unix milliseconds).</summary>
    private sealed record Cached(long Stamp, long SentAt, UnifiResponse Response);

    /// <summary>What goes over the wire for <see cref="SystemLogQuery"/>.</summary>
    private sealed record SystemLogBody(long TimestampFrom, long TimestampTo, int PageSize, int PageNumber,
        string[]? Categories, string[]? Events, string[]? Severities, string? SearchText);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private double _tokens = Burst;
    private long? _refilled;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TimeZoneInfo Zone, string Name)> _zones = new(StringComparer.Ordinal);

    /// <summary>A site by its id, name or internal name; omitted means the configured site.</summary>
    public async Task<ConsoleSite> SiteAsync(string? hint, CancellationToken ct)
    {
        var site = await client.FindSiteAsync(hint, ct);
        if (site.Reference is { } reference && SafeReference().IsMatch(reference)) return new ConsoleSite(this, reference, site.Name);
        throw new UnifiError($"The console has no internal name for site '{site.Name}' that this server can use.");
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeReference();

    /// <summary>
    /// Every read ends here: cached under <paramref name="cacheKey"/>, one at a time, and charged against the budget.
    /// The body is made from the wall clock (Unix milliseconds) when the console is asked, which also comes back, so a
    /// read over "the last hours" is cached by its question, not its times. Cache age and the budget follow the steady
    /// clock, so setting the wall clock back or forward neither keeps an answer nor spends or refills the budget.
    /// Private, so the reads <see cref="ConsoleSite"/> offers are the only requests that can exist, and their paths are
    /// fixed but for the site's checked internal name and numbers.
    /// </summary>
    private async Task<(UnifiResponse Response, long SentAt)> SendAsync(HttpMethod method, string path, string cacheKey, Func<long, string?> body, CancellationToken ct)
    {
        if (client.Settings.Missing().Count > 0) // throws "not configured"
        {
            var at = time.GetUtcNow().ToUnixTimeMilliseconds();
            return (await client.SendInternalAsync(method, path, body(at), ct), at);
        }

        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetTimestamp();
            bool Fresh(Cached c) => time.GetElapsedTime(c.Stamp, now) < CacheFor;
            if (_cache.TryGetValue(cacheKey, out var hit) && Fresh(hit)) return (hit.Response, hit.SentAt);
            foreach (var stale in _cache.Where(c => !Fresh(c.Value)).Select(c => c.Key).ToList()) _cache.Remove(stale);

            if (_refilled is { } last)
                _tokens = Math.Min(Burst, _tokens + time.GetElapsedTime(last, now).TotalSeconds * PerMinute / 60.0);
            _refilled = now;
            if (_tokens < 1)
            {
                var wait = Math.Ceiling((1 - _tokens) * 60.0 / PerMinute);
                throw new UnifiError($"Slow down: the console's internal API is limited to {PerMinute} calls a minute (bursts of {Burst}) so polling cannot destabilise the network; try again in {wait:0} s.");
            }
            _tokens -= 1;

            var sentAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            var response = await client.SendInternalAsync(method, path, body(sentAt), ct);
            if (response.Ok) _cache[cacheKey] = new Cached(now, sentAt, response);
            return (response, sentAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>An answer as JSON; non-2xx and a <c>meta.rc</c> other than ok become a <see cref="UnifiError"/>.</summary>
    internal async Task<JsonElement> JsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var text = body is null ? null : JsonSerializer.Serialize(body, Json);
        var (r, _) = await SendAsync(method, path, $"{method} {path}\n{text}", _ => text, ct);
        return Parse(method, path, r);
    }

    /// <summary>
    /// A page of the system log over the last hours up to now. Cached by the question (the site's path, the hours, the
    /// page and the filters), and the window that comes back is the one its data covers.
    /// </summary>
    internal async Task<SystemLogPage> SystemLogAsync(string path, SystemLogQuery q, CancellationToken ct)
    {
        var span = q.Hours * 3_600_000L;
        var (r, to) = await SendAsync(HttpMethod.Post, path, $"{HttpMethod.Post} {path}\nlast {JsonSerializer.Serialize(q, Json)}",
            now => JsonSerializer.Serialize(new SystemLogBody(now - span, now, q.PageSize, q.PageNumber, q.Categories, q.Events, q.Severities, q.SearchText), Json), ct);
        return new SystemLogPage(Parse(HttpMethod.Post, path, r), new LogWindow(q.Hours, to - span, to));
    }

    private static JsonElement Parse(HttpMethod method, string path, UnifiResponse r)
    {
        var name = $"{method} {path.Split('?')[0]}";
        if (!r.Ok) throw new UnifiError($"The console answered {r.Status} to {name}: {Short(Redaction.Error(r.Body))}");
        JsonElement json;
        try { json = JsonDocument.Parse(r.Body).RootElement; }
        catch (JsonException) { throw new UnifiError($"The console's answer to {name} was not JSON."); }
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("meta", out var meta) && TextOrNull(meta, "rc") is { } rc && rc != "ok")
            throw new UnifiError($"The console refused {name}: {TextOrNull(meta, "msg") ?? rc}.");
        return json;
    }

    /// <summary>The <c>data</c> array of a classic answer (<c>{"meta":…,"data":[…]}</c>).</summary>
    internal async Task<List<JsonElement>> DataAsync(string path, CancellationToken ct)
    {
        var json = await JsonAsync(HttpMethod.Get, path, null, ct);
        if (json.ValueKind == JsonValueKind.Array) return json.EnumerateArray().ToList();
        if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) return data.EnumerateArray().ToList();
        throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not the list this server expects.");
    }

    /// <summary>
    /// A site's own time zone (from stat/sysinfo, only that field), so times read as they do in the console's UI. Kept per
    /// site once known; UTC while unknown, asked again next time.
    /// </summary>
    internal async Task<(TimeZoneInfo Zone, string Name)> TimeZoneAsync(string reference, CancellationToken ct)
    {
        if (_zones.TryGetValue(reference, out var known)) return known;
        try
        {
            var info = await DataAsync($"/api/s/{reference}/stat/sysinfo", ct);
            if (info.Count > 0 && TextOrNull(info[0], "timezone") is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
                return _zones[reference] = (zone, id);
        }
        catch (UnifiError)
        {
            // Times fall back to UTC; the tool's answer says which.
        }
        return (TimeZoneInfo.Utc, "UTC");
    }
}

/// <summary>One site's reads from the console's internal API. Cheap to make: what lasts lives in <see cref="ConsoleReads"/>.</summary>
public sealed class ConsoleSite
{
    /// <summary>How far back client history reaches: the 30 days the console keeps.</summary>
    public const int HistoryHours = 720;

    private readonly ConsoleReads _reads;

    internal ConsoleSite(ConsoleReads reads, string reference, string name) => (_reads, Reference, Name) = (reads, reference, name);

    /// <summary>The internal API's name for the site ("default").</summary>
    public string Reference { get; }

    public string Name { get; }

    private string Classic => $"/api/s/{Reference}";
    private string V2 => $"/v2/api/site/{Reference}";

    /// <summary>The console's time zone and its name, so times read as they do in its UI; UTC when it cannot say.</summary>
    public Task<(TimeZoneInfo Zone, string Name)> TimeZoneAsync(CancellationToken ct) => _reads.TimeZoneAsync(Reference, ct);

    /// <summary>The gateway's subsystems and monitors.</summary>
    public Task<List<JsonElement>> HealthAsync(CancellationToken ct) => _reads.DataAsync($"{Classic}/stat/health", ct);

    /// <summary>Every adopted device with its ports, radios and stats.</summary>
    public Task<List<JsonElement>> DevicesAsync(CancellationToken ct) => _reads.DataAsync($"{Classic}/stat/device", ct);

    /// <summary>Connected clients as the console sees them (switch port, signal, fixed IP).</summary>
    public Task<List<JsonElement>> StationsAsync(CancellationToken ct) => _reads.DataAsync($"{Classic}/stat/sta", ct);

    /// <summary>Neighbouring networks the APs heard in the last 24 hours.</summary>
    public Task<List<JsonElement>> RogueApsAsync(CancellationToken ct) => _reads.DataAsync($"{Classic}/stat/rogueap?within=24", ct);

    public Task<List<JsonElement>> PortForwardsAsync(CancellationToken ct) => _reads.DataAsync($"{Classic}/rest/portforward", ct);

    /// <summary>Clients seen in the last <see cref="HistoryHours"/>, online or not, blocked ones left out.</summary>
    public Task<List<JsonElement>> ClientHistoryAsync(CancellationToken ct) =>
        _reads.DataAsync($"{V2}/clients/history?onlyNonBlocked=true&withinHours={HistoryHours}", ct);

    /// <summary>The dashboard's WAN details and history over the last <paramref name="hours"/>.</summary>
    public Task<JsonElement> DashboardAsync(int hours, CancellationToken ct) =>
        _reads.JsonAsync(HttpMethod.Get, $"{V2}/aggregated-dashboard?historySeconds={hours * 3600}", null, ct);

    /// <summary>Each WAN link's failover state.</summary>
    public Task<JsonElement> WanStatusAsync(CancellationToken ct) => _reads.JsonAsync(HttpMethod.Get, $"{V2}/wan/load-balancing/status", null, ct);

    /// <summary>A page of the system log over the last hours, with the window it covers.</summary>
    public Task<SystemLogPage> SystemLogAsync(SystemLogQuery query, CancellationToken ct) => _reads.SystemLogAsync($"{V2}/system-log/all", query, ct);

    /// <summary>Event counts per category and event over a window, such as the one a page of the log covered.</summary>
    public Task<JsonElement> SystemLogCountAsync(LogWindow window, CancellationToken ct) =>
        _reads.JsonAsync(HttpMethod.Post, $"{V2}/system-log/count", new { timestampFrom = window.From, timestampTo = window.To }, ct);
}
