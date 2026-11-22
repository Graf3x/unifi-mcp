using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
        CancellationToken ct = default) => RunView(siteId, ct, async (site, sb) =>
    {
        var devices = await site.DevicesAsync(ct);
        if (string.IsNullOrWhiteSpace(device))
        {
            var ordered = devices.OrderBy(d => V(d, "state") == "1").ThenBy(d => V(d, "name")).ToList();
            var down = devices.Count(d => V(d, "state") != "1");
            sb.AppendLine($"## Devices: {devices.Count}, {down} not online");
            sb.AppendLine(FaqTools.Table(["Device", "Model", "Type", "State", "Uptime", "CPU %", "Memory %", "Temp °C", "Firmware", "Issues"],
                ordered.Select(d => new[]
                {
                    V(d, "name"), V(d, "model"), V(d, "type"), DeviceState(V(d, "state")), V(d, "state") == "1" ? Duration(Long(d, "uptime")) : "",
                    Obj(d, "system-stats") is { } s ? V(s, "cpu") : "", Obj(d, "system-stats") is { } m ? V(m, "mem") : "", Temperature(d),
                    Bool(d, "upgradable") ? $"{V(d, "version")} → {First(V(d, "upgrade_to_firmware"), "update available")}" : V(d, "version"),
                    string.Join(", ", Issues(d)),
                })));
            sb.AppendLine("Call again with device set to one name for its ports, PoE and uplink.");
            return NewView("devices", siteId) with { Devices = ordered.Select(Tile).ToList() };
        }

        var q = device.Trim();
        var found = devices.Where(d => string.Equals(V(d, "mac"), q, StringComparison.OrdinalIgnoreCase) || V(d, "ip") == q
                                       || V(d, "name").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0)
        {
            var none = $"No device matches '{device}'. Devices: {string.Join(", ", devices.Select(d => V(d, "name")))}.";
            sb.AppendLine(none);
            return NewView("message", siteId) with { Message = none };
        }
        if (found.Count > 1)
        {
            var several = $"'{device}' matches {found.Count} devices; use the IP or MAC";
            sb.AppendLine($"{several}:\n" + FaqTools.Table(["Name", "IP", "MAC"], found.Select(d => new[] { V(d, "name"), V(d, "ip"), V(d, "mac") })));
            return NewView("message", siteId) with
            {
                Message = several + ".",
                Candidates = found.Select(d => new Candidate(V(d, "name"), V(d, "ip"), V(d, "mac"))).ToList(),
            };
        }

        var dev = found[0];
        sb.AppendLine($"## {V(dev, "name")} ({V(dev, "model")}, {V(dev, "type")})");
        sb.AppendLine($"- State {DeviceState(V(dev, "state"))}, IP {V(dev, "ip")}, MAC {V(dev, "mac")}, firmware {V(dev, "version")}"
                      + (Bool(dev, "upgradable") ? $" (update to {First(V(dev, "upgrade_to_firmware"), "a newer version")} available)." : "."));
        if (Obj(dev, "system-stats") is { } stats)
            sb.AppendLine($"- CPU {V(stats, "cpu")} %, memory {V(stats, "mem")} %, up {Duration(Long(dev, "uptime"))}.");
        var temps = Array(dev, "temperatures");
        if (temps.Count > 0) sb.AppendLine("- Temperatures: " + string.Join(", ", temps.Select(t => $"{V(t, "name")} {V(t, "value")} °C")) + ".");
        else if (V(dev, "general_temperature") is { Length: > 0 } general) sb.AppendLine($"- Temperature {general} °C.");
        if (Bool(dev, "overheating")) sb.AppendLine("- **Overheating.**");
        if (Bool(dev, "has_fan")) sb.AppendLine($"- Fan level {First(V(dev, "fan_level"), "?")}.");
        if (V(dev, "total_max_power") is { Length: > 0 } budget && Dbl(dev, "total_max_power") > 0) sb.AppendLine($"- PoE: {First(V(dev, "total_used_power"), "?")} W used of {budget} W.");
        if (Obj(dev, "uplink") is { } up && V(up, "type").Length > 0)
            sb.AppendLine($"- Uplink: {V(up, "type")} " + (V(up, "uplink_device_name") is { Length: > 0 } upName ? $"to {upName} port {First(V(up, "uplink_remote_port"), "?")}" : $"on {First(V(up, "comment"), V(up, "name"), "?")}") + $", {First(V(up, "speed"), "?")} Mbps"
                          + (Bool(up, "full_duplex") ? " full duplex" : "") + $", errors rx {First(V(up, "rx_errors"), "0")} / tx {First(V(up, "tx_errors"), "0")}.");
        foreach (var s in Array(dev, "storage")) sb.AppendLine($"- Storage {V(s, "name")}: {Pct(Dbl(s, "used"), Dbl(s, "size"))} % used.");
        sb.AppendLine();

        var radios = Array(dev, "radio_table_stats");
        if (radios.Count > 0)
            sb.AppendLine("## Radios").AppendLine(FaqTools.Table(["Band", "Channel", "Width (MHz)", "Tx power", "Clients", "Channel use %", "Interference %", "Retries %"],
                radios.Select(r => new[] { Band(V(r, "radio")), V(r, "channel"), V(r, "bw"), V(r, "tx_power"), V(r, "num_sta"), V(r, "cu_total"), V(r, "cu_interf"), V(r, "tx_retries_pct") })));

        var ports = Array(dev, "port_table");
        if (ports.Count > 0)
        {
            var active = ports.Where(p => Bool(p, "up") || PortFlags(p).Count > 0).ToList();
            sb.AppendLine($"## Ports: {ports.Count(p => Bool(p, "up"))} up of {ports.Count}");
            sb.AppendLine(FaqTools.Table(["Port", "Name", "Link", "Speed (Mbps)", "Duplex", "PoE W", "Rx errors", "Tx errors", "Drops", "Link downs", "STP", "Flags"], active.Select(p => new[]
            {
                V(p, "port_idx"), V(p, "name"), Bool(p, "up") ? "up" : Bool(p, "enable") ? "down" : "disabled", V(p, "speed"),
                Bool(p, "up") ? (Bool(p, "full_duplex") ? "full" : "half") : "", Bool(p, "poe_enable") ? V(p, "poe_power") : "",
                V(p, "rx_errors"), V(p, "tx_errors"), ((Dbl(p, "rx_dropped") ?? 0) + (Dbl(p, "tx_dropped") ?? 0)).ToString(CultureInfo.InvariantCulture),
                V(p, "link_down_count"), V(p, "stp_state"), string.Join(", ", PortFlags(p)),
            })));
            var idle = ports.Except(active).Select(p => V(p, "port_idx")).ToList();
            if (idle.Count > 0) sb.AppendLine($"Down with nothing to report: {string.Join(", ", idle)}.");
        }

        return NewView("device", siteId) with { Device = Detail(dev, []) };
    });

    /// <summary>
    /// Like <see cref="Run"/>, and the steps also return the view's data. A console failure is the answer, not a tool
    /// error: the text is its reason and the view shows the same sentence.
    /// </summary>
    private async Task<CallToolResult> RunView(string? siteId, CancellationToken ct, Func<ConsoleSite, StringBuilder, Task<DiagnosticsView>> steps)
    {
        var sb = new StringBuilder();
        DiagnosticsView view;
        try
        {
            view = await steps(await reads.SiteAsync(siteId, ct), sb);
        }
        catch (Exception e) when (e is UnifiError or UnifiNotConfigured)
        {
            sb.AppendLine(e.Message);
            view = NewView("message", siteId) with { Message = e.Message };
        }
        return ViewResult.Create(sb.ToString(), view);
    }

    private DiagnosticsView NewView(string kind, string? siteId) =>
        new(kind, siteId, time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private static DeviceTile Tile(JsonElement d) => new(
        V(d, "name"), V(d, "mac"), V(d, "model"), V(d, "type"), DeviceState(V(d, "state")), Uptime(d), Stat(d, "cpu"), Stat(d, "mem"),
        Hottest(d), V(d, "version"), UpdateTo(d), Issues(d).ToList());

    private static DeviceDetail Detail(JsonElement d, IReadOnlyList<PortView> ports)
    {
        var poe = Dbl(d, "total_max_power") is > 0;
        return new(V(d, "name"), V(d, "mac"), V(d, "model"), V(d, "type"), DeviceState(V(d, "state")), V(d, "ip"), V(d, "version"), UpdateTo(d),
            Uptime(d), Stat(d, "cpu"), Stat(d, "mem"), Hottest(d), Bool(d, "overheating"),
            poe ? Dbl(d, "total_used_power") : null, poe ? Dbl(d, "total_max_power") : null,
            Obj(d, "uplink") is { } up && V(up, "type").Length > 0
                ? new UplinkFacts(V(up, "type"), NullIfEmpty(V(up, "uplink_device_name")), NullIfEmpty(V(up, "uplink_remote_port")), Long(up, "speed"))
                : null,
            ports);
    }

    private static long? Uptime(JsonElement d) => V(d, "state") == "1" ? Long(d, "uptime") : null;

    private static double? Stat(JsonElement d, string name) => Obj(d, "system-stats") is { } s ? Dbl(s, name) : null;

    private static string? UpdateTo(JsonElement d) => Bool(d, "upgradable") ? First(V(d, "upgrade_to_firmware"), "available") : null;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static List<string> PortFlags(JsonElement p)
    {
        var flags = new List<string>();
        if ((Dbl(p, "rx_errors") ?? 0) + (Dbl(p, "tx_errors") ?? 0) > 0) flags.Add("errors");
        if ((Dbl(p, "link_down_count") ?? 0) >= 5) flags.Add("flapping");
        if (Bool(p, "up") && !Bool(p, "full_duplex")) flags.Add("half duplex");
        if (Bool(p, "up") && Dbl(p, "speed") is > 0 and <= 100) flags.Add("slow link");
        return flags;
    }

    private static IEnumerable<string> Issues(JsonElement d)
    {
        if (Bool(d, "overheating")) yield return "overheating";
        var bad = Array(d, "port_table").Where(p => PortFlags(p).Count > 0).Select(p => V(p, "port_idx")).ToList();
        if (bad.Count > 0) yield return $"port(s) {string.Join(", ", bad)} flagged";
        if (Obj(d, "uplink") is { } up && (Dbl(up, "rx_errors") ?? 0) + (Dbl(up, "tx_errors") ?? 0) > 0) yield return "uplink errors";
    }

    /// <summary>The hottest sensor, else the device's one general reading.</summary>
    private static double? Hottest(JsonElement d)
    {
        var temps = Array(d, "temperatures").Select(t => Dbl(t, "value")).OfType<double>().ToList();
        return temps.Count > 0 ? temps.Max() : Dbl(d, "general_temperature");
    }

    private static string Temperature(JsonElement d)
    {
        var temps = Array(d, "temperatures").Select(t => Dbl(t, "value")).OfType<double>().ToList();
        return temps.Count > 0 ? temps.Max().ToString(CultureInfo.InvariantCulture) : V(d, "general_temperature");
    }

    private static string DeviceState(string state) => state switch
    {
        "0" => "offline", "1" => "online", "2" => "pending adoption", "4" => "upgrading", "5" => "provisioning",
        "6" => "heartbeat missed", "7" => "adopting", "9" => "adoption failed", "10" or "11" => "isolated", _ => $"state {state}",
    };
}
