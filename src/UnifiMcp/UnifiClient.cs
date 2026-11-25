using System.Collections.Concurrent;
using System.Text.Json;
using static UnifiMcp.ConsoleJson;

namespace UnifiMcp;

/// <summary>The call could not be made or the console refused it. The message is safe to show the caller.</summary>
public sealed class UnifiError(string message) : Exception(message);

/// <summary>The owner has not supplied the API key or certificate pin yet.</summary>
public sealed class UnifiNotConfigured(string message) : Exception(message);

public sealed record UnifiResponse(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>A site on the console: its id (the Integration API's name for it), its internal name (the Internal API's) and its display name.</summary>
public sealed record Site(string Id, string? Reference, string Name);

/// <summary>
/// The console's two APIs over one <see cref="ConsoleTransport"/>. Non-GET requests are refused here as well as by tool
/// registration while writes are off. The console's internal API (the one its own UI uses) is read only through
/// <see cref="ConsoleReads"/>.
/// </summary>
public sealed class UnifiClient(Settings settings, ConsoleTransport transport)
{
    private const string ApiRoot = "/proxy/network/integration";
    private const string ConsoleRoot = "/proxy/network";
    private const int PageSize = 200;

    /// <summary>The most items one paged list is read to: 25 pages of 200.</summary>
    public const int MaxItems = 5_000;

    /// <summary>Sites looked up so far, by the hint that named them, and whether the hint was the site's id.</summary>
    private readonly ConcurrentDictionary<string, (Site Site, bool ById)> _sites = new(StringComparer.OrdinalIgnoreCase);

    public Settings Settings => settings;

    public Task<UnifiResponse> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (method != HttpMethod.Get && !settings.AllowWrites)
            throw new UnifiError("Writes are disabled (UNIFI_ALLOW_WRITES is not true), so nothing was sent.");
        return transport.SendAsync(method, ApiRoot + path, body, ct);
    }

    /// <summary>A request to the internal API, relative to /proxy/network. Only <see cref="ConsoleReads"/> sends these.</summary>
    internal Task<UnifiResponse> SendInternalAsync(HttpMethod method, string path, string? body, CancellationToken ct) =>
        transport.SendAsync(method, ConsoleRoot + path, body, ct);

    public async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, path, null, ct);
        if (!r.Ok) throw new UnifiError($"The console answered {r.Status} to GET {path.Split('?')[0]}: {Short(Redaction.Error(r.Body))}");
        try { return JsonDocument.Parse(r.Body).RootElement; }
        catch (JsonException) { throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not JSON."); }
    }

    /// <summary>The items of a paged list as read, and whether they are all of it: false when this server's limit cut the list short.</summary>
    public sealed record PagedList(List<JsonElement> Items, bool Complete);

    /// <summary>The items of a paged list, as <see cref="GetListAsync"/> reads them, for a caller that needs no more than the answer's partial note.</summary>
    public async Task<List<JsonElement>> GetAllAsync(string path, CancellationToken ct) => (await GetListAsync(path, ct)).Items;

    /// <summary>
    /// Every item of a paged list, up to <see cref="MaxItems"/> in at most <see cref="MaxItems"/> / 200 pages. It ends at
    /// the console's latest total or at an empty page, so a total that changes while it reads cannot keep it going. A list
    /// cut short is noted in the answer's <see cref="UpstreamBudget"/>, so the answer says it is partial, and comes back
    /// not <see cref="PagedList.Complete"/>, so a caller can tell an item missing from the list from one cut off it.
    /// </summary>
    public async Task<PagedList> GetListAsync(string path, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var sep = path.Contains('?') ? '&' : '?';
        for (int offset = 0, pages = 0; ;)
        {
            var page = await GetJsonAsync($"{path}{sep}offset={offset}&limit={PageSize}", ct);
            try
            {
                var data = page.GetProperty("data").EnumerateArray().ToList();
                var room = MaxItems - all.Count;
                all.AddRange(data.Take(room));
                offset += data.Count;
                var total = page.GetProperty("totalCount").GetInt64();
                // A page bigger than asked for (a console that ignores limit) can hold more than there is room for.
                var dropped = data.Count > room;
                if (!dropped && (data.Count == 0 || offset >= total)) return new(all, true);
                if (dropped || all.Count >= MaxItems || ++pages >= MaxItems / PageSize)
                {
                    UpstreamBudget.Current?.Note($"Only the first {all.Count} of {Math.Max(total, offset)} items of GET {path.Split('?')[0]} were read "
                                                 + $"(this server reads at most {MaxItems} per list). {UpstreamBudget.Narrow}");
                    return new(all, false);
                }
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new UnifiError($"The console's answer to GET {path.Split('?')[0]} was not the paged list this server expects.");
            }
        }
    }

    /// <summary>
    /// A site by its id, name or internal name, ignoring case; omitted means the configured site. An id wins; a name or
    /// internal name must belong to one site only, or the lookup is refused with the candidates; on a console with more
    /// sites than <see cref="MaxItems"/>, where that cannot be known, only an id is taken. A read looks a hint up
    /// once and keeps the answer. A write looks a name up again on every call, since a site can be renamed and its old
    /// name given to another; only an id, which never moves to another site, is taken from what was looked up before.
    /// </summary>
    public Task<Site> FindSiteAsync(string? hint, CancellationToken ct) => FindSiteAsync(hint, write: false, ct);

    /// <inheritdoc cref="FindSiteAsync(string?, CancellationToken)"/>
    public async Task<Site> FindSiteAsync(string? hint, bool write, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(hint) ? settings.Site : hint.Trim();
        if (_sites.TryGetValue(wanted, out var known) && (!write || known.ById)) return known.Site;

        // A lookup made outside a composite answer (a generated operation, the guide, the setup page) gets a budget of its
        // own, so it is bounded like any answer; set here, it ends when this method returns.
        if (UpstreamBudget.Current is null) UpstreamBudget.Open();
        var list = await GetListAsync("/v1/sites", ct);
        var sites = list.Items.Where(s => TextOrNull(s, "id") is { Length: > 0 }).ToList();
        // What a name meant before may be stale now, so a fresh list replaces everything looked up from an older one.
        _sites.Clear();
        bool Is(JsonElement s, string field) => string.Equals(TextOrNull(s, field), wanted, StringComparison.OrdinalIgnoreCase);
        // An id names one site, so it wins over another site whose name or internal name happens to be that id.
        if (sites.FirstOrDefault(s => Is(s, "id")) is { ValueKind: JsonValueKind.Object } byId)
            return (_sites[wanted] = (ToSite(byId), true)).Site;
        // Past the cut there may be the site whose id this is, or a second site with this name, so a name is not enough.
        if (!list.Complete)
            throw new UnifiError($"The console has more sites than the {MaxItems} this server reads, and none of those has the id '{wanted}', "
                                 + "so it was not taken as a name: name the site by its id.");
        var named = sites.Where(s => Is(s, "name") || Is(s, "internalReference")).ToList();
        if (named.Count > 1)
            throw new UnifiError($"More than one site is called '{wanted}', so none was chosen. Name the site by its id: "
                                 + string.Join("; ", named.Select(s => Describe(ToSite(s)))) + ".");
        if (named.Count == 1) return (_sites[wanted] = (ToSite(named[0]), false)).Site;
        throw new UnifiError($"The console has no site '{wanted}'. Sites: {string.Join(", ", sites.Select(s => TextOrNull(s, "name")))}.");
    }

    private static Site ToSite(JsonElement s) =>
        new(TextOrNull(s, "id")!, TextOrNull(s, "internalReference"), TextOrNull(s, "name") ?? TextOrNull(s, "id")!);

    private static string Describe(Site s) =>
        s.Reference is { } reference ? $"'{s.Name}' (id {s.Id}, internal name '{reference}')" : $"'{s.Name}' (id {s.Id})";
}
