using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static UnifiMcp.ConsoleJson;

namespace UnifiMcp;

// device_diagnostics_faq. The text answer and the view's data are built in one pass from the same values, so the two
// never disagree.
public sealed partial class DiagnosticTools
{
    [McpServerTool(Name = "device_diagnostics_faq", Title = "Device diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Hardware health of UniFi devices. Without a device: every gateway, switch and AP with state, uptime, CPU, memory, temperature, firmware updates and flagged ports. "
                 + "With a device (name, IP or MAC): its CPU, memory, temperatures, fan, PoE budget, uplink and storage, plus every active port with what is connected to it, speed, duplex, PoE draw, "
                 + "errors, drops, link-down count and STP state, with bad ports flagged. Use for 'is this switch dropping packets' or 'which port is flapping'.")]
    [McpMeta("ui", JsonValue = "{\"resourceUri\":\"" + ViewResources.DeviceDiagnosticsUri + "\"}")]
    public Task<CallToolResult> DeviceDiagnostics(
        [Description("Device name (partial match), IP address or MAC address. Omit for a summary of every device.")] string? device = null,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var site = await reads.SiteAsync(siteId, ct);
        var devices = await site.DevicesAsync(ct);
        if (string.IsNullOrWhiteSpace(device))
        {
            var ordered = devices.OrderBy(d => Text(d, "state") == "1").ThenBy(d => Text(d, "name")).ToList();
            var down = devices.Count(d => Text(d, "state") != "1");
            a.Line($"## Devices: {devices.Count}, {down} not online");
            a.Line(Answer.Table(["Device", "Model", "Type", "State", "Uptime", "CPU %", "Memory %", "Temp °C", "Firmware", "Issues"],
                ordered.Select(d => new[]
                {
                    Text(d, "name"), Text(d, "model"), Text(d, "type"), DeviceState(Text(d, "state")), Text(d, "state") == "1" ? Duration(Long(d, "uptime")) : "",
                    Obj(d, "system-stats") is { } s ? Text(s, "cpu") : "", Obj(d, "system-stats") is { } m ? Text(m, "mem") : "", Temperature(d),
                    Bool(d, "upgradable") ? $"{Text(d, "version")} → {First(Text(d, "upgrade_to_firmware"), "update available")}" : Text(d, "version"),
                    string.Join(", ", Issues(d)),
                })));
            a.Line("Call again with device set to one name for its ports, PoE and uplink.");
            a.View = NewView("devices", siteId) with { Devices = ordered.Select(Tile).ToList() };
            return;
        }

        var q = device.Trim();
        var found = devices.Where(d => string.Equals(Text(d, "mac"), q, StringComparison.OrdinalIgnoreCase) || Text(d, "ip") == q
                                       || Text(d, "name").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0)
        {
            var none = $"No device matches '{device}'. Devices: {string.Join(", ", devices.Select(d => Text(d, "name")))}.";
            a.Line(none);
            a.View = NewView("message", siteId) with { Message = none };
            return;
        }
        if (found.Count > 1)
        {
            var several = $"'{device}' matches {found.Count} devices; use the IP or MAC";
            a.Line($"{several}:\n" + Answer.Table(["Name", "IP", "MAC"], found.Select(d => new[] { Text(d, "name"), Text(d, "ip"), Text(d, "mac") })));
            a.View = NewView("message", siteId) with
            {
                Message = several + ".",
                Candidates = found.Select(d => new Candidate(Text(d, "name"), Text(d, "ip"), Text(d, "mac"))).ToList(),
            };
            return;
        }

        var dev = found[0];
        a.Line($"## {Text(dev, "name")} ({Text(dev, "model")}, {Text(dev, "type")})");
        a.Line($"- State {DeviceState(Text(dev, "state"))}, IP {Text(dev, "ip")}, MAC {Text(dev, "mac")}, firmware {Text(dev, "version")}"
                      + (Bool(dev, "upgradable") ? $" (update to {First(Text(dev, "upgrade_to_firmware"), "a newer version")} available)." : "."));
        if (Obj(dev, "system-stats") is { } stats)
            a.Line($"- CPU {Text(stats, "cpu")} %, memory {Text(stats, "mem")} %, up {Duration(Long(dev, "uptime"))}.");
        var temps = Items(dev, "temperatures");
        if (temps.Count > 0) a.Line("- Temperatures: " + string.Join(", ", temps.Select(t => $"{Text(t, "name")} {Text(t, "value")} °C")) + ".");
        else if (Text(dev, "general_temperature") is { Length: > 0 } general) a.Line($"- Temperature {general} °C.");
        if (Bool(dev, "overheating")) a.Line("- **Overheating.**");
        if (Bool(dev, "has_fan")) a.Line($"- Fan level {First(Text(dev, "fan_level"), "?")}.");
        if (Text(dev, "total_max_power") is { Length: > 0 } budget && Dbl(dev, "total_max_power") > 0) a.Line($"- PoE: {First(Text(dev, "total_used_power"), "?")} W used of {budget} W.");
        if (Obj(dev, "uplink") is { } up && Text(up, "type").Length > 0)
            a.Line($"- Uplink: {Text(up, "type")} " + (Text(up, "uplink_device_name") is { Length: > 0 } upName ? $"to {upName} port {First(Text(up, "uplink_remote_port"), "?")}" : $"on {First(Text(up, "comment"), Text(up, "name"), "?")}") + $", {First(Text(up, "speed"), "?")} Mbps"
                          + (Bool(up, "full_duplex") ? " full duplex" : "") + $", errors rx {First(Text(up, "rx_errors"), "0")} / tx {First(Text(up, "tx_errors"), "0")}.");
        foreach (var s in Items(dev, "storage")) a.Line($"- Storage {Text(s, "name")}: {Pct(Dbl(s, "used"), Dbl(s, "size"))} % used.");
        a.Line();

        var radios = Items(dev, "radio_table_stats");
        if (radios.Count > 0)
            a.Line("## Radios").Line(Answer.Table(["Band", "Channel", "Width (MHz)", "Tx power", "Clients", "Channel use %", "Interference %", "Retries %"],
                radios.Select(r => new[] { Band(Text(r, "radio")), Text(r, "channel"), Text(r, "bw"), Text(r, "tx_power"), Text(r, "num_sta"), Text(r, "cu_total"), Text(r, "cu_interf"), Text(r, "tx_retries_pct") })));

        var ports = Items(dev, "port_table");
        var portViews = new List<PortView>();
        if (ports.Count > 0)
        {
            // Two more reads, each a step that may fail without losing the rest: who is on each port, and what each port can do.
            var connected = ConnectedDevices(devices, dev);
            if (Upstream(devices, dev, ports) is { } upstream) Add(connected, upstream.Port, upstream.Device);
            var stations = await a.Step("Connected clients", () => site.StationsAsync(ct)) ?? [];
            foreach (var c in stations.Where(c => Bool(c, "is_wired") && SameMac(Text(c, "sw_mac"), Text(dev, "mac")) && Text(c, "sw_port").Length > 0).OrderBy(Name))
                Add(connected, Text(c, "sw_port"), new Connected("client", Name(c), Text(c, "mac")));
            var capabilities = await a.Step("Port capabilities", () => PortCapabilitiesAsync(siteId, Text(dev, "mac"), ct)) ?? [];

            var rows = ports.Select(p =>
            {
                var idx = Text(p, "port_idx");
                var (max, connector) = capabilities.TryGetValue(idx, out var cap) ? cap : (null, "RJ45");
                return (Json: p, View: Port(p, connected.TryGetValue(idx, out var on) ? on : [], max, connector));
            }).ToList();
            portViews = rows.Select(r => r.View).ToList();

            var active = rows.Where(r => r.View.State == "up" || r.View.Flags.Count > 0).ToList();
            a.Line($"## Ports: {ports.Count(p => Bool(p, "up"))} up of {ports.Count}");
            a.Line(Answer.Table(["Port", "Name", "Connected", "Link", "Speed (Mbps)", "Max (Mbps)", "Duplex", "PoE W", "Rx errors", "Tx errors", "Drops", "Link downs", "STP", "Flags"], active.Select(r =>
            {
                var (p, v) = r;
                return new[]
                {
                    Text(p, "port_idx"), Text(p, "name"), ConnectedCell(v), v.State, Text(p, "speed"), v.MaxSpeedMbps?.ToString(CultureInfo.InvariantCulture) ?? "",
                    v.Duplex ?? "", Bool(p, "poe_enable") ? Text(p, "poe_power") : "",
                    Text(p, "rx_errors"), Text(p, "tx_errors"), ((Dbl(p, "rx_dropped") ?? 0) + (Dbl(p, "tx_dropped") ?? 0)).ToString(CultureInfo.InvariantCulture),
                    Text(p, "link_down_count"), Text(p, "stp_state"), string.Join(", ", v.Flags),
                };
            })));
            var idle = rows.Where(r => r.View.State != "up" && r.View.Flags.Count == 0).Select(r => Text(r.Json, "port_idx")).ToList();
            if (idle.Count > 0) a.Line($"Down with nothing to report: {string.Join(", ", idle)}.");
        }

        a.View = NewView("device", siteId) with { Device = Detail(dev, portViews) };
    }, onFatal: message => NewView("message", siteId) with { Message = message });

    // --- ports ------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal) { "RJ45", "SFP", "SFPPLUS", "SFP28", "QSFP28" };

    /// <summary>
    /// The UniFi devices plugged into this one, by its port number. No extra read: every device names its parent and the
    /// parent's port in its own uplink.
    /// </summary>
    private static Dictionary<string, List<Connected>> ConnectedDevices(List<JsonElement> devices, JsonElement parent)
    {
        var byPort = new Dictionary<string, List<Connected>>(StringComparer.Ordinal);
        string mac = Text(parent, "mac"), name = Text(parent, "name");
        foreach (var d in devices.OrderBy(d => Text(d, "name")))
        {
            if (SameMac(Text(d, "mac"), mac) || Obj(d, "uplink") is not { } up || Text(up, "uplink_remote_port") is not { Length: > 0 } port) continue;
            var isChild = Text(up, "uplink_mac") is { Length: > 0 } parentMac ? SameMac(parentMac, mac) : name.Length > 0 && Text(up, "uplink_device_name") == name;
            if (isChild) Add(byPort, port, new Connected("device", Text(d, "name"), Text(d, "mac")));
        }
        return byPort;
    }

    /// <summary>
    /// The device upstream of this one and the local port it is on, so the uplink port does not read as empty. The far
    /// end comes from the device's own uplink; the local port from the uplink too, else from the one port the console
    /// marks as the uplink. Nothing when either is unknown.
    /// </summary>
    private static (string Port, Connected Device)? Upstream(List<JsonElement> devices, JsonElement dev, List<JsonElement> ports)
    {
        if (Obj(dev, "uplink") is not { } up) return null;
        string name = Text(up, "uplink_device_name"), mac = Text(up, "uplink_mac");
        var parent = devices.FirstOrDefault(d => mac.Length > 0 ? SameMac(Text(d, "mac"), mac) : name.Length > 0 && Text(d, "name") == name);
        name = First(name, Text(parent, "name"));
        if (name.Length == 0) return null;
        var marked = ports.Where(p => Bool(p, "is_uplink")).Select(p => Text(p, "port_idx")).ToList();
        var port = First(Text(up, "port_idx"), marked.Count == 1 ? marked[0] : "");
        return port.Length == 0 ? null : (port, new Connected("device", name, First(mac, Text(parent, "mac"))));
    }

    private static void Add(Dictionary<string, List<Connected>> byPort, string port, Connected item)
    {
        if (!byPort.TryGetValue(port, out var list)) byPort[port] = list = [];
        list.Add(item);
    }

    /// <summary>
    /// What each port can do, by port number, from the official API's device detail (the console's own port table does
    /// not say): its maximum speed and its connector. A device the official list does not have gives an empty answer.
    /// </summary>
    private async Task<Dictionary<string, (long? Max, string Connector)>> PortCapabilitiesAsync(string? siteId, string mac, CancellationToken ct)
    {
        var capabilities = new Dictionary<string, (long? Max, string Connector)>(StringComparer.Ordinal);
        var site = await console.FindSiteAsync(siteId, ct);
        var listed = (await console.GetAllAsync($"/v1/sites/{site.Id}/devices", ct)).FirstOrDefault(d => SameMac(Text(d, "macAddress"), mac));
        if (!Guid.TryParse(Text(listed, "id"), out var id)) return capabilities; // not listed, or an id this server will not put in a path
        var detail = await console.GetJsonAsync($"/v1/sites/{site.Id}/devices/{id}", ct);
        foreach (var p in Items(Obj(detail, "interfaces"), "ports"))
            capabilities[Text(p, "idx")] = (Long(p, "maxSpeedMbps"), Connectors.Contains(Text(p, "connector")) ? Text(p, "connector") : "RJ45");
        return capabilities;
    }

    private static PortView Port(JsonElement p, List<Connected> connected, long? max, string connector)
    {
        var up = Bool(p, "up");
        long? speed = up && Long(p, "speed") is > 0 and var s ? s : null;
        var flags = PortFlags(p, max);
        long rx = Long(p, "rx_errors") ?? 0, tx = Long(p, "tx_errors") ?? 0, downs = Long(p, "link_down_count") ?? 0;
        return new((int)(Long(p, "port_idx") ?? 0), Text(p, "name"), up ? "up" : Bool(p, "enable") ? "down" : "disabled", speed, max, connector,
            up ? (Bool(p, "full_duplex") ? "full" : "half") : null, Bool(p, "poe_enable") ? Dbl(p, "poe_power") : null, rx, tx,
            (long)((Dbl(p, "rx_dropped") ?? 0) + (Dbl(p, "tx_dropped") ?? 0)), downs, NullIfEmpty(Text(p, "stp_state")), flags,
            PortNote(flags, speed, max, rx, tx, downs), connected.Take(5).ToList(), connected.Count);
    }

    private static string ConnectedCell(PortView port) =>
        string.Join(", ", port.Connected.Select(c => c.Name)) + (port.ConnectedCount > port.Connected.Count ? $" +{port.ConnectedCount - port.Connected.Count} more" : "");

    /// <summary>MAC addresses compare by their hex digits: the console writes them lower case, the official API upper.</summary>
    private static bool SameMac(string a, string b)
    {
        static string Hex(string mac) => string.Concat(mac.Where(Uri.IsHexDigit)).ToLowerInvariant();
        return a.Length > 0 && Hex(a) == Hex(b);
    }

    /// <summary>A speed the way people say it: 100 Mb, 1 GbE, 2.5 GbE.</summary>
    internal static string SpeedLabel(long? mbps) => mbps switch
    {
        null => "?",
        < 1000 => $"{mbps} Mb",
        _ => (mbps.Value / 1000.0).ToString(CultureInfo.InvariantCulture) + " GbE",
    };

    /// <summary>One plain sentence for a flagged port, from its flags and, when known, what the port can do. None for a port without flags.</summary>
    internal static string? PortNote(IReadOnlyList<string> flags, long? speed, long? max, long rxErrors, long txErrors, long linkDowns)
    {
        if (flags.Count == 0) return null;
        var parts = new List<string>();
        if (flags.Contains("half duplex") || flags.Contains("slow link"))
            parts.Add($"linked at {SpeedLabel(speed)}{(flags.Contains("half duplex") ? " half duplex" : "")}"
                      + (max > speed ? $" on a port that can do {SpeedLabel(max)}" : ""));
        if (flags.Contains("errors"))
            parts.Add(rxErrors > 0 && txErrors > 0 ? "receive and send errors" : rxErrors > 0 ? "receive errors" : "send errors");
        if (flags.Contains("flapping")) parts.Add($"link dropped {linkDowns} times since the device restarted");
        var cause = flags.Contains("errors") || flags.Contains("half duplex") ? "suspect the cable or the device's network card"
            : flags.Contains("flapping") ? "check the cable and the device's power"
            : "the device or its cable may only support this speed";
        var s = string.Join("; ", parts);
        return char.ToUpperInvariant(s[0]) + s[1..] + ": " + cause + ".";
    }

    private DiagnosticsView NewView(string kind, string? siteId) =>
        new(kind, siteId, time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private static DeviceTile Tile(JsonElement d) => new(
        Text(d, "name"), Text(d, "mac"), Text(d, "model"), Text(d, "type"), DeviceState(Text(d, "state")), Uptime(d), Stat(d, "cpu"), Stat(d, "mem"),
        Hottest(d), Text(d, "version"), UpdateTo(d), Issues(d).ToList());

    private static DeviceDetail Detail(JsonElement d, IReadOnlyList<PortView> ports)
    {
        var poe = Dbl(d, "total_max_power") is > 0;
        return new(Text(d, "name"), Text(d, "mac"), Text(d, "model"), Text(d, "type"), DeviceState(Text(d, "state")), Text(d, "ip"), Text(d, "version"), UpdateTo(d),
            Uptime(d), Stat(d, "cpu"), Stat(d, "mem"), Hottest(d), Bool(d, "overheating"),
            poe ? Dbl(d, "total_used_power") : null, poe ? Dbl(d, "total_max_power") : null,
            Obj(d, "uplink") is { } up && Text(up, "type").Length > 0
                ? new UplinkFacts(Text(up, "type"), NullIfEmpty(Text(up, "uplink_device_name")), NullIfEmpty(Text(up, "uplink_remote_port")), Long(up, "speed"))
                : null,
            ports);
    }

    private static long? Uptime(JsonElement d) => Text(d, "state") == "1" ? Long(d, "uptime") : null;

    private static double? Stat(JsonElement d, string name) => Obj(d, "system-stats") is { } s ? Dbl(s, name) : null;

    private static string? UpdateTo(JsonElement d) => Bool(d, "upgradable") ? First(Text(d, "upgrade_to_firmware"), "available") : null;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>
    /// The one place a port's flags are decided, for the text and the view alike. A port at 100 Mb or less is a slow
    /// link unless that is all the port can do (<paramref name="maxSpeedMbps"/>, null when unknown).
    /// </summary>
    internal static List<string> PortFlags(JsonElement p, long? maxSpeedMbps)
    {
        var flags = new List<string>();
        if ((Dbl(p, "rx_errors") ?? 0) + (Dbl(p, "tx_errors") ?? 0) > 0) flags.Add("errors");
        if ((Dbl(p, "link_down_count") ?? 0) >= 5) flags.Add("flapping");
        if (Bool(p, "up") && !Bool(p, "full_duplex")) flags.Add("half duplex");
        if (Bool(p, "up") && Dbl(p, "speed") is > 0 and <= 100 && maxSpeedMbps is not <= 100) flags.Add("slow link");
        return flags;
    }

    private static IEnumerable<string> Issues(JsonElement d)
    {
        if (Bool(d, "overheating")) yield return "overheating";
        var bad = Items(d, "port_table").Where(p => PortFlags(p, null).Count > 0).Select(p => Text(p, "port_idx")).ToList();
        if (bad.Count > 0) yield return $"port(s) {string.Join(", ", bad)} flagged";
        if (Obj(d, "uplink") is { } up && (Dbl(up, "rx_errors") ?? 0) + (Dbl(up, "tx_errors") ?? 0) > 0) yield return "uplink errors";
    }

    /// <summary>The hottest sensor, else the device's one general reading.</summary>
    private static double? Hottest(JsonElement d)
    {
        var temps = Items(d, "temperatures").Select(t => Dbl(t, "value")).OfType<double>().ToList();
        return temps.Count > 0 ? temps.Max() : Dbl(d, "general_temperature");
    }

    private static string Temperature(JsonElement d)
    {
        var temps = Items(d, "temperatures").Select(t => Dbl(t, "value")).OfType<double>().ToList();
        return temps.Count > 0 ? temps.Max().ToString(CultureInfo.InvariantCulture) : Text(d, "general_temperature");
    }

    private static string DeviceState(string state) => state switch
    {
        "0" => "offline", "1" => "online", "2" => "pending adoption", "4" => "upgrading", "5" => "provisioning",
        "6" => "heartbeat missed", "7" => "adopting", "9" => "adoption failed", "10" or "11" => "isolated", _ => $"state {state}",
    };
}
