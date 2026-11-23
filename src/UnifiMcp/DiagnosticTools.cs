using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>
/// Diagnostics from the console's internal API (the one its own UI uses), which the official API does not cover:
/// the system log, WAN quality, Wi-Fi radio health, a client's signal and switch port, device ports and temperatures,
/// port forwards. Reads only, through <see cref="ConsoleReads"/>, which caches and throttles them; device diagnostics also asks
/// the official API what each port can do. Answers are built
/// from named fields; raw objects are never passed through, because they carry secrets (device keys, a maps API key).
/// </summary>
[McpServerToolType]
public sealed partial class DiagnosticTools(UnifiClient console, ConsoleReads reads, TimeProvider time)
{
    private const int MaxHours = 720;

    [McpServerTool(Name = "system_log", Title = "System log", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Logs] The console's System Log, newest first: client connects, disconnects and roams, blocked threats, admin logins, AP channel changes and radar, WAN packet loss and failover, device alerts. "
                 + "Use it to answer 'what happened' and 'when'. Filters run on the console: category (e.g. CLIENT_DEVICES, UNIFI_DEVICES, SECURITY, INTERNET_AND_WAN, AUDIT), event (e.g. CLIENT_ROAMED, THREAT_BLOCKED), severity (LOW, MEDIUM, HIGH, VERY_HIGH) and search (a name, IP or MAC). "
                 + "Called without filters it also returns counts per category and event for the window, so a second call can filter precisely. Times are in the console's time zone.")]
    public Task<CallToolResult> SystemLog(
        [Description("How far back to look, in hours (1 to 720). Default 24.")] int hours = 24,
        [Description("Comma-separated categories, e.g. 'SECURITY' or 'CLIENT_DEVICES,UNIFI_DEVICES'.")] string? category = null,
        [Description("Comma-separated event names, e.g. 'THREAT_BLOCKED' or 'CLIENT_CONNECTED_WIRED,CLIENT_DISCONNECTED_WIRED'.")] string? @event = null,
        [Description("Comma-separated severities: LOW, MEDIUM, HIGH, VERY_HIGH.")] string? severity = null,
        [Description("Text the console matches against each event, e.g. a client name, IP or MAC.")] string? search = null,
        [Description("Events per page (1 to 200). Default 50.")] int limit = 50,
        [Description("Page number, starting at 0.")] int page = 0,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var site = await reads.SiteAsync(siteId, ct);
        var zone = await site.TimeZoneAsync(ct);
        var window = Window(hours);
        var query = LogQuery(window, limit, page, List(category), List(@event), List(severity), search);
        var result = await site.SystemLogAsync(query, ct);
        var events = Array(result, "data").Take(query.PageSize).ToList();
        var total = Long(result, "total_element_count") ?? events.Count;
        var pages = Long(result, "total_page_count") ?? 1;

        var filters = new[] { ("category", category), ("event", @event), ("severity", severity), ("search", search) }.Where(f => !string.IsNullOrWhiteSpace(f.Item2)).ToList();
        a.Write($"# System log: {total} event(s) in the last {window.Hours} h");
        a.Line(filters.Count == 0 ? "" : " matching " + string.Join(", ", filters.Select(f => $"{f.Item1} '{f.Item2}'")));
        a.Line($"Showing {events.Count} (page {page} of {Math.Max(pages, 1)}, newest first). Times are console local time ({zone.Name}).");
        if ((page + 1L) * query.PageSize < total)
            a.Line($"More: call again with page={page + 1}, or narrow with category, event, severity or search.");
        a.Line().Line(LogTable(events, zone.Zone));

        if (filters.Count == 0 && page == 0)
        {
            try
            {
                var counts = await site.SystemLogCountAsync(window.From, window.To, ct);
                a.Line("## Counts in this window");
                a.Line("Categories: " + Counts(counts, "categories"));
                a.Line("Events: " + Counts(counts, "events"));
            }
            catch (UnifiError e)
            {
                a.Line($"(Counts unavailable: {e.Message})");
            }
        }
    });

    [McpServerTool(Name = "internet_health_faq", Title = "Internet and WAN health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Is the internet connection healthy: each WAN link and whether it is active or backup, the ISP and plan, the gateway's availability and latency monitors, "
                 + "5-minute latency and packet-loss history with the worst intervals, outages, the last speed tests, and recent WAN events from the system log. "
                 + "Use for 'is the internet slow or dropping'. Markdown in steps; a failed step does not stop the rest.")]
    public Task<CallToolResult> InternetHealth(
        [Description("History window in hours (1 to 168). Default 24.")] int hours = 24,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var site = await reads.SiteAsync(siteId, ct);
        hours = Math.Clamp(hours, 1, 168);
        var zone = await site.TimeZoneAsync(ct);
        var dash = await a.Step("Dashboard", () => site.DashboardAsync(hours, ct));
        var lb = await a.Step("WAN status", () => site.WanStatusAsync(ct));

        a.Line("## WAN links");
        var details = dash is { } d && d.TryGetProperty("wan", out var wan) ? Array(wan, "wan_details") : [];
        var states = lb is { } l ? Array(l, "wan_interfaces") : [];
        var groups = states.Select(s => V(s, "wan_networkgroup")).Concat(details.Select(x => V(x, "network_group"))).Where(g => g.Length > 0).Distinct().ToList();
        a.Line(Answer.Table(["WAN", "Group", "Failover state", "ISP", "Interface", "Link", "Plan down/up (Mbps)", "Service latency (ms)"], groups.Select(g =>
        {
            var s = states.FirstOrDefault(x => V(x, "wan_networkgroup") == g);
            var x = details.FirstOrDefault(y => V(y, "network_group") == g);
            var isp = Obj(x, "isp");
            var caps = Obj(isp, "capabilities");
            var status = Obj(x, "status");
            var plan = caps is { } c ? $"{Kbps(c, "download_kilobits_per_second")}/{Kbps(c, "upload_kilobits_per_second")}" : "";
            var latencies = Obj(x, "stats") is { } st ? string.Join(", ", Array(st, "service_latencies").Select(sl => $"{V(sl, "service_name")} {V(sl, "latency")}")) : "";
            return new[] { First(V(s, "name"), V(x, "network_name")), g, V(s, "state"), isp is { } i ? V(i, "name") : "", status is { } t ? V(t, "interface_name") : "", status is { } u ? V(u, "state") : "", plan, latencies };
        })));

        var health = await a.Step("Gateway monitors", () => site.HealthAsync(ct));
        if (health is not null)
        {
            var wanHealth = health.FirstOrDefault(h => V(h, "subsystem") == "wan");
            var www = health.FirstOrDefault(h => V(h, "subsystem") == "www");
            a.Line("## Gateway monitors");
            if (wanHealth.ValueKind == JsonValueKind.Object)
            {
                a.Line($"WAN IP {V(wanHealth, "wan_ip")}, ISP {V(wanHealth, "isp_name")}, status {V(wanHealth, "status")}.");
                if (Obj(wanHealth, "uptime_stats") is { } us)
                    foreach (var link in us.EnumerateObject())
                    {
                        a.Line($"{link.Name}: availability {V(link.Value, "availability", 2)} % and average latency {V(link.Value, "latency_average")} ms over the last {Long(link.Value, "time_period") / 3600 ?? 24} h.");
                        a.Line(Answer.Table(["Target", "Type", "Availability %", "Latency (ms)"], Array(link.Value, "monitors").Select(m =>
                            new[] { V(m, "target"), V(m, "type"), V(m, "availability", 2), V(m, "latency_average") })));
                    }
            }
            if (www.ValueKind == JsonValueKind.Object)
                a.Line($"Internet check: latency {V(www, "latency")} ms, {V(www, "drops")} drop(s), last speed test {V(www, "xput_down")} down / {V(www, "xput_up")} up Mbps"
                              + (Long(www, "speedtest_lastrun") is { } run ? $" at {Seconds(run, zone.Zone)}." : "."));
            a.Line();
        }

        if (dash is { } board)
        {
            a.Line($"## Last {hours} h (5-minute samples, times {zone.Name})");
            var groupsActivity = Obj(Obj(board, "wan_activity"), "activity_by_network_group");
            if (groupsActivity is { } ga)
                foreach (var g in ga.EnumerateObject())
                {
                    var samples = Array(g.Value, "history");
                    if (samples.Count == 0) continue;
                    var loss = samples.Select(x => Dbl(x, "avg_packet_loss_pct") ?? 0).ToList();
                    var latency = samples.Select(x => Dbl(x, "avg_latency_ms")).OfType<double>().ToList();
                    a.Line($"**{g.Name}**: {samples.Count} samples. Latency average {Avg(latency)} ms, highest {Max(samples, "max_latency_ms")} ms. "
                                  + $"Packet loss average {Avg(loss)} %, {loss.Count(v => v >= 2)} sample(s) at 2 % or more.");
                    var worst = samples.Where(x => (Dbl(x, "avg_packet_loss_pct") ?? 0) > 0 || (Dbl(x, "max_latency_ms") ?? 0) > 2 * Math.Max(1, latency.DefaultIfEmpty(0).Average()))
                        .OrderByDescending(x => Dbl(x, "avg_packet_loss_pct") ?? 0).ThenByDescending(x => Dbl(x, "max_latency_ms") ?? 0).Take(5).ToList();
                    if (worst.Count > 0)
                        a.Line(Answer.Table(["Worst intervals", "Packet loss %", "Avg latency (ms)", "Max latency (ms)"], worst.Select(x =>
                            new[] { Millis(Long(x, "timestamp"), zone.Zone), V(x, "avg_packet_loss_pct"), V(x, "avg_latency_ms"), V(x, "max_latency_ms") })));
                }
            var outages = Obj(board, "wan_history") is { } wh ? Array(wh, "wan_history_details").SelectMany(x => Array(x, "downtime_history")).ToList() : [];
            a.Line($"{outages.Count} outage(s) recorded in this window.");
            foreach (var o in outages.Take(10))
            {
                var parts = o.ValueKind == JsonValueKind.Object ? o.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    .Select(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var ms) && ms > 1_000_000_000_000 ? $"{p.Name} {Millis(ms, zone.Zone)}" : $"{p.Name} {p.Value}") : [];
                a.Line("- " + string.Join(", ", parts));
            }
            a.Line();

            var tests = Obj(board, "speed_test") is { } stt ? Array(stt, "data").Where(x => Long(x, "time") is not null).ToList() : [];
            a.Line("## Speed tests");
            a.Line(tests.Count == 0 ? "None in this window." : Answer.Table(["When", "WAN", "Down (Mbps)", "Up (Mbps)", "Latency (ms)"], tests.Select(x =>
                new[] { Millis(Long(x, "time"), zone.Zone), V(x, "wan_networkgroup"), V(x, "download_mbps"), V(x, "upload_mbps"), V(x, "latency_ms") })));
        }

        var events = await a.Step("WAN events", () => site.SystemLogAsync(
            LogQuery(Window(hours), 15, 0, ["INTERNET_AND_WAN"], [], [], null), ct));
        if (events is { } ev)
            a.Line($"## WAN events ({Long(ev, "total_element_count") ?? 0} in the last {hours} h, newest first)").Line(LogTable(Array(ev, "data").Take(15).ToList(), zone.Zone));
    });

    [McpServerTool(Name = "wifi_health_faq", Title = "Wi-Fi health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Wi-Fi health per access point and radio: channel, width, transmit power, clients, channel utilisation and interference, retry rate and satisfaction, "
                 + "how many neighbouring networks are strong on the same channel, which APs are offline, and recent channel changes and radar events. "
                 + "Problems are flagged (busy channel, interference, high retries). Use for 'why is Wi-Fi slow' or 'which channel should this AP use'.")]
    public Task<CallToolResult> WifiHealth(
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var site = await reads.SiteAsync(siteId, ct);
        var devices = await a.Step("Access points", () => site.DevicesAsync(ct));
        if (devices is null) return;
        var aps = devices.Where(d => V(d, "type") == "uap").ToList();
        var neighbours = await a.Step("Neighbouring networks", () => site.RogueApsAsync(ct)) ?? [];

        var online = aps.Where(a => V(a, "state") == "1").ToList();
        a.Line($"## Radios: {online.Count} access point(s) online");
        a.Line(Answer.Table(["AP", "Band", "Channel", "Width (MHz)", "Tx power", "Clients", "Channel use %", "Interference %", "Retries %", "Satisfaction", "Neighbours on channel (≥ -80 dBm)", "Flags"],
            online.SelectMany(ap => Array(ap, "radio_table_stats").Select(r =>
            {
                var strong = neighbours.Where(n => V(n, "ap_mac") == V(ap, "mac") && V(n, "channel") == V(r, "channel") && (Dbl(n, "signal") ?? -100) >= -80).Select(n => V(n, "bssid")).Distinct().Count();
                var flags = new List<string>();
                if ((Dbl(r, "cu_total") ?? 0) >= 50) flags.Add("busy channel");
                if ((Dbl(r, "cu_interf") ?? 0) >= 30) flags.Add("interference");
                if ((Dbl(r, "tx_retries_pct") ?? 0) >= 15) flags.Add("high retries");
                if (Dbl(r, "satisfaction") is >= 0 and < 80) flags.Add("low satisfaction");
                return new[] { V(ap, "name"), Band(V(r, "radio")), V(r, "channel"), V(r, "bw"), V(r, "tx_power"), V(r, "num_sta"), V(r, "cu_total"), V(r, "cu_interf"),
                    V(r, "tx_retries_pct"), Dbl(r, "satisfaction") is >= 0 ? V(r, "satisfaction") : "", $"{strong} neighbour(s)", string.Join(", ", flags) };
            }))));
        var offline = aps.Where(a => V(a, "state") != "1").ToList();
        if (offline.Count > 0)
            a.Line($"**Not online ({offline.Count}):** " + string.Join(", ", offline.Select(a => $"{V(a, "name")} ({V(a, "model")}, {DeviceState(V(a, "state"))})"))).Line();

        var zone = await site.TimeZoneAsync(ct);
        var events = await a.Step("Radio events", () => site.SystemLogAsync(
            LogQuery(Window(24), 15, 0, [], ["AP_CHANGED_CHANNELS", "AP_DETECTED_RADAR"], [], null), ct));
        if (events is { } ev)
            a.Line($"## Channel changes and radar, last 24 h ({Long(ev, "total_element_count") ?? 0})").Line(LogTable(Array(ev, "data").Take(15).ToList(), zone.Zone));
    });

    [McpServerTool(Name = "client_diagnostics_faq", Title = "Client diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Everything about one client: identity and vendor, its IP and any fixed-IP reservation, how it connects (switch and exact port with link speed, "
                 + "or the AP, SSID, band, channel, signal, noise, rates and retries), its quality score, and its recent system-log events (connects, disconnects, roams). "
                 + "Finds clients that are offline too (seen in the last 30 days). Use for 'why does this device keep dropping' or 'where is this device plugged in'.")]
    public Task<CallToolResult> ClientDiagnostics(
        [Description("Client name or hostname (partial match), IP address or MAC address.")] string client,
        [Description("How far back to read its events, in hours (1 to 720). Default 24.")] int hours = 24,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var site = await reads.SiteAsync(siteId, ct);
        var q = (client ?? "").Trim();
        bool Matches(JsonElement x, params string[] names) =>
            string.Equals(V(x, "mac"), q, StringComparison.OrdinalIgnoreCase) || V(x, "ip") == q || V(x, "last_ip") == q
            || names.Any(n => V(x, n).Contains(q, StringComparison.OrdinalIgnoreCase) && q.Length > 0);

        var stations = await site.StationsAsync(ct);
        var found = stations.Where(x => Matches(x, "name", "hostname")).ToList();
        var online = found.Count > 0;
        if (!online)
            found = (await site.ClientHistoryAsync(ct))
                .Where(x => Matches(x, "name", "display_name", "hostname")).ToList();
        if (found.Count == 0)
        {
            a.Line($"No client matches '{client}' among connected clients or those seen in the last 30 days.");
            return;
        }
        if (found.Count > 1)
        {
            a.Line($"'{client}' matches {found.Count} clients; use the IP or MAC:").Line(Answer.Table(["Name", "IP", "MAC", "State"],
                found.Select(x => new[] { Name(x), First(V(x, "ip"), V(x, "last_ip")), V(x, "mac"), online ? "online" : "offline" })));
            return;
        }

        var zone = await site.TimeZoneAsync(ct);
        var c = found[0];
        a.Line($"## {Name(c)} ({(online ? "online" : "offline")})");
        a.Line($"- MAC {V(c, "mac")}, vendor {First(V(c, "oui"), "unknown")}, hostname {First(V(c, "hostname"), "none")}.");
        a.Line($"- Network {First(V(c, "network"), V(c, "last_connection_network_name"), V(c, "network_name"))}, IP {First(V(c, "ip"), V(c, "last_ip"), "none")}.");
        a.Line(Bool(c, "use_fixedip") ? $"- Fixed IP reservation: {V(c, "fixed_ip")}." : "- Fixed IP reservation: none.");
        if (online)
            a.Line($"- Connected for {Duration(Long(c, "uptime"))}; satisfaction {First(V(c, "satisfaction"), "?")} %.");
        else
            a.Line($"- Offline. Last seen {Seconds(Long(c, "last_seen"), zone.Zone)} ({zone.Name}) on {First(V(c, "last_uplink_name"), "an unknown device")}.");
        a.Line();

        if (online)
        {
            a.Line("## Connection");
            if (Bool(c, "is_wired"))
                a.Line($"Wired to {First(V(c, "last_uplink_name"), "a switch")} port {First(V(c, "sw_port"), "?")} at {First(V(c, "wired_rate_mbps"), "?")} Mbps.");
            else
            {
                var signal = Dbl(c, "signal");
                var retries = Dbl(c, "wifi_tx_retries_percentage");
                a.Line($"Wi-Fi '{V(c, "essid")}' on {V(c, "last_uplink_name")}: {Band(V(c, "radio"))}, channel {V(c, "channel")}, {First(V(c, "channel_width"), "?")} MHz.");
                a.Line($"Signal {V(c, "signal")} dBm, noise {V(c, "noise")} dBm; rates {Rate(c, "tx_rate")} tx / {Rate(c, "rx_rate")} rx Mbps; retries {First(V(c, "wifi_tx_retries_percentage"), "?")} %.");
                if (signal < -75) a.Line("**Weak signal:** below -75 dBm. Move the client or AP closer, or check what is between them.");
                if (retries >= 15) a.Line("**High retries:** 15 % or more of transmissions are repeated, which usually means interference or a weak link.");
            }
            a.Line();
        }

        var events = await a.Step("Events", () => site.SystemLogAsync(
            LogQuery(Window(hours), 15, 0, [], [], [], V(c, "mac")), ct));
        if (events is { } ev)
            a.Line($"## Its events, last {Window(hours).Hours} h ({Long(ev, "total_element_count") ?? 0}, newest first)").Line(LogTable(Array(ev, "data").Take(15).ToList(), zone.Zone));
    });

    [McpServerTool(Name = "list_port_forwards", Title = "List port forwards", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Firewall] Port forwarding rules (not in the official API): name, enabled, protocol, the WAN port, the internal address and port it forwards to, "
                 + "which sources may use it and on which WAN. Use for 'why can't I reach my server from outside'.")]
    public Task<CallToolResult> PortForwards(
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var rules = await (await reads.SiteAsync(siteId, ct)).PortForwardsAsync(ct);
        if (rules.Count == 0)
        {
            a.Write("No port forwards are configured.");
            return;
        }
        a.Write($"## Port forwards: {rules.Count}\n" + Answer.Table(["Name", "State", "Protocol", "WAN port", "Forward to", "Allowed from", "WAN", "Logged"], rules.Select(r => new[]
        {
            V(r, "name"), Bool(r, "enabled") ? "enabled" : "disabled", V(r, "proto") == "tcp_udp" ? "TCP/UDP" : V(r, "proto").ToUpperInvariant(),
            V(r, "dst_port"), $"{V(r, "fwd")}:{V(r, "fwd_port")}", First(V(r, "src"), "any"), First(V(r, "pfwd_interface"), "wan"), Bool(r, "log") ? "yes" : "no",
        })));
    });

    // --- the system log ---------------------------------------------------------------------------------------------

    private sealed record LogWindow(int Hours, long From, long To);

    /// <summary>The last <paramref name="hours"/> up to now by the host's clock.</summary>
    private LogWindow Window(int hours)
    {
        var h = Math.Clamp(hours, 1, MaxHours);
        var to = time.GetUtcNow().ToUnixTimeMilliseconds();
        return new LogWindow(h, to - h * 3_600_000L, to);
    }

    private static SystemLogQuery LogQuery(LogWindow w, int limit, int page, string[] categories, string[] events, string[] severities, string? search) =>
        new(w.From, w.To, Math.Clamp(limit, 1, 200), Math.Max(page, 0),
            categories.Length == 0 ? null : categories, events.Length == 0 ? null : events, severities.Length == 0 ? null : severities,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim());

    private static string[] List(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant().Replace(' ', '_')).ToArray();

    private static string LogTable(List<JsonElement> events, TimeZoneInfo zone) => events.Count == 0 ? "No events.\n" :
        Answer.Table(["Time", "Severity", "Category", "Event", "Message"], events.Select(e => new[]
        {
            Millis(Long(e, "timestamp"), zone), V(e, "severity"), V(e, "category"), First(V(e, "event"), V(e, "key")), Message(e),
        }));

    /// <summary>The console stores a template ("{CLIENT} roamed from {DEVICE_FROM}…") and its parameters; this fills it in.</summary>
    internal static string Message(JsonElement e)
    {
        var template = First(V(e, "message"), V(e, "message_raw"), V(e, "title_raw"));
        var parameters = Obj(e, "parameters");
        var text = Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (parameters is { } p && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object)
                return First(V(v, "name"), V(v, "id"), m.Value);
            if (key == "ADMIN" && Obj(e, "admin") is { } admin) return V(admin, "name");
            if (key == "IP" && V(e, "ip") is { Length: > 0 } ip) return ip;
            if (key == "PLATFORM" && V(e, "platform") is { Length: > 0 } platform) return platform.ToLowerInvariant();
            return m.Value;
        });
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    [GeneratedRegex(@"\{([A-Z0-9_]+)\}")]
    private static partial Regex Placeholder();

    private static string Counts(JsonElement counts, string name) =>
        string.Join(", ", Array(counts, name).OrderByDescending(c => Long(c, "count") ?? 0).Select(c => $"{V(c, "name")} {V(c, "count")}"));

    // --- small readers: every value shown is read by name, never by passing an object through -----------------------

    internal static string V(JsonElement e, string name, int? decimals = null)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.Trim() ?? "",
            JsonValueKind.Number when decimals is { } dp && v.TryGetDouble(out var d) => Math.Round(d, dp).ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => "",
        };
    }

    private static JsonElement? Obj(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static List<JsonElement> Array(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    private static double? Dbl(JsonElement e, string name) =>
        double.TryParse(V(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? Long(JsonElement e, string name) => Dbl(e, name) is { } d ? (long)d : null;

    private static bool Bool(JsonElement e, string name) => V(e, name) == "true";

    private static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    private static string Name(JsonElement c) => First(V(c, "name"), V(c, "display_name"), V(c, "hostname"), V(c, "mac"));

    private static string Band(string radio) => radio switch { "ng" => "2.4 GHz", "na" => "5 GHz", "6e" => "6 GHz", _ => radio };

    private static string Kbps(JsonElement e, string name) => Dbl(e, name) is { } k ? Math.Round(k / 1000).ToString(CultureInfo.InvariantCulture) : "?";

    private static string Rate(JsonElement e, string name) => Dbl(e, name) is { } k ? Math.Round(k / 1000, 1).ToString(CultureInfo.InvariantCulture) : "?";

    private static string Avg(List<double> values) => values.Count == 0 ? "?" : Math.Round(values.Average(), 1).ToString(CultureInfo.InvariantCulture);

    private static string Max(List<JsonElement> samples, string name) =>
        samples.Select(s => Dbl(s, name)).OfType<double>().DefaultIfEmpty().Max().ToString(CultureInfo.InvariantCulture);

    private static string Pct(double? part, double? whole) => part is { } p && whole is > 0 ? Math.Round(100 * p / whole.Value).ToString(CultureInfo.InvariantCulture) : "?";

    private static string Millis(long? ms, TimeZoneInfo zone) =>
        ms is { } v ? TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(v), zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?";

    private static string Seconds(long? s, TimeZoneInfo zone) => s is { } v ? Millis(v * 1000, zone) : "?";

    private static string Duration(long? seconds)
    {
        if (seconds is not { } s || s < 0) return "?";
        var t = TimeSpan.FromSeconds(s);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min" : $"{t.Minutes} min";
    }
}
