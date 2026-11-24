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
            var ordered = devices.OrderBy(d => DeviceReading.IsOnline(d)).ThenBy(d => Text(d, "name")).ToList();
            var down = devices.Count(d => !DeviceReading.IsOnline(d));
            a.Line($"## Devices: {devices.Count}, {down} not online");
            a.Line(Answer.Table(["Device", "Model", "Type", "State", "Uptime", "CPU %", "Memory %", "Temp °C", "Firmware", "Issues"],
                ordered.Select(d => new[]
                {
                    Text(d, "name"), Text(d, "model"), Text(d, "type"), DeviceReading.StateName(d), DeviceReading.IsOnline(d) ? Duration(Long(d, "uptime")) : "",
                    Obj(d, "system-stats") is { } s ? Text(s, "cpu") : "", Obj(d, "system-stats") is { } m ? Text(m, "mem") : "", Temperature(d),
                    Bool(d, "upgradable") ? $"{Text(d, "version")} → {First(Text(d, "upgrade_to_firmware"), "update available")}" : Text(d, "version"),
                    string.Join(", ", DeviceReading.Tile(d).Issues),
                })));
            a.Line("Call again with device set to one name for its ports, PoE and uplink.");
            a.View = NewView("devices", siteId) with { Devices = ordered.Select(DeviceReading.Tile).ToList() };
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
        a.Line($"- State {DeviceReading.StateName(dev)}, IP {Text(dev, "ip")}, MAC {Text(dev, "mac")}, firmware {Text(dev, "version")}"
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
        var stations = new List<JsonElement>();
        var capabilities = new Dictionary<string, PortCapability>();
        if (ports.Count > 0)
        {
            // Two more reads, each a step that may fail without losing the rest: who is on each port, and what each port can do.
            stations = await a.Step("Connected clients", () => site.StationsAsync(ct)) ?? [];
            capabilities = await a.Step("Port capabilities", () => PortCapabilitiesAsync(siteId, Text(dev, "mac"), ct)) ?? [];
        }
        var detail = DeviceReading.Detail(dev, devices, stations, capabilities);

        if (ports.Count > 0)
        {
            var rows = ports.Zip(detail.Ports, (p, v) => (Json: p, View: v)).ToList();
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

        a.View = NewView("device", siteId) with { Device = detail };
    }, onFatal: message => NewView("message", siteId) with { Message = message });

    // --- ports ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What each port can do, by port number, from the official API's device detail (the console's own port table does
    /// not say). A device the official list does not have gives an empty answer.
    /// </summary>
    private async Task<Dictionary<string, PortCapability>> PortCapabilitiesAsync(string? siteId, string mac, CancellationToken ct)
    {
        var site = await console.FindSiteAsync(siteId, ct);
        var listed = (await console.GetAllAsync($"/v1/sites/{site.Id}/devices", ct)).FirstOrDefault(d => DeviceReading.SameMac(Text(d, "macAddress"), mac));
        if (!Guid.TryParse(Text(listed, "id"), out var id)) return []; // not listed, or an id this server will not put in a path
        return DeviceReading.Capabilities(await console.GetJsonAsync($"/v1/sites/{site.Id}/devices/{id}", ct));
    }

    private static string ConnectedCell(PortView port) =>
        string.Join(", ", port.Connected.Select(c => c.Name)) + (port.ConnectedCount > port.Connected.Count ? $" +{port.ConnectedCount - port.Connected.Count} more" : "");

    private DiagnosticsView NewView(string kind, string? siteId) =>
        new(kind, siteId, time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private static string Temperature(JsonElement d)
    {
        var temps = Items(d, "temperatures").Select(t => Dbl(t, "value")).OfType<double>().ToList();
        return temps.Count > 0 ? temps.Max().ToString(CultureInfo.InvariantCulture) : Text(d, "general_temperature");
    }
}
