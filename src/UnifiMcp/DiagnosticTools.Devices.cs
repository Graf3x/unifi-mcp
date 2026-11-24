using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static UnifiMcp.ConsoleJson;

namespace UnifiMcp;

// device_diagnostics_faq. DeviceReading makes the facts of each device; the text answer below and the view both render
// them, so the two never disagree.
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
            var tiles = devices.OrderBy(DeviceReading.IsOnline).ThenBy(d => Text(d, "name")).Select(DeviceReading.Tile).ToList();
            a.Line($"## Devices: {devices.Count}, {devices.Count(d => !DeviceReading.IsOnline(d))} not online");
            a.Line(Answer.Table(["Device", "Model", "Type", "State", "Uptime", "CPU %", "Memory %", "Temp °C", "Firmware", "Issues"],
                tiles.Select(t => new[]
                {
                    t.Name, t.Model, t.Type, t.State, t.UptimeSeconds is { } up ? Duration(up) : "", Num(t.CpuPct), Num(t.MemoryPct), Num(t.TemperatureC),
                    t.UpdateTo is { } to ? $"{t.Firmware} → {(to == DeviceReading.UnknownVersion ? "update available" : to)}" : t.Firmware,
                    string.Join(", ", t.Issues),
                })));
            a.Line("Call again with device set to one name for its ports, PoE and uplink.");
            a.View = NewView("devices", siteId) with { Devices = tiles };
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
        var stations = new List<JsonElement>();
        var capabilities = new Dictionary<string, PortCapability>();
        if (Items(dev, "port_table").Count > 0)
        {
            // Two more reads, each a step that may fail without losing the rest: who is on each port, and what each port can do.
            stations = await a.Step("Connected clients", () => site.StationsAsync(ct)) ?? [];
            capabilities = await a.Step("Port capabilities", () => PortCapabilitiesAsync(siteId, Text(dev, "mac"), ct)) ?? [];
        }
        var d = DeviceReading.Detail(dev, devices, stations, capabilities);
        DetailText(a, d);
        a.View = NewView("device", siteId) with { Device = d };
    }, onFatal: message => NewView("message", siteId) with { Message = message });

    /// <summary>One device's text answer, from its reading.</summary>
    private static void DetailText(Answer a, DeviceDetail d)
    {
        a.Line($"## {d.Name} ({d.Model}, {d.Type})");
        a.Line($"- State {d.State}, IP {d.Ip}, MAC {d.Mac}, firmware {d.Firmware}"
               + (d.UpdateTo is { } to ? $" (update to {(to == DeviceReading.UnknownVersion ? "a newer version" : to)} available)." : "."));
        if (d.CpuPct is not null || d.MemoryPct is not null)
            a.Line($"- CPU {Num(d.CpuPct, "?")} %, memory {Num(d.MemoryPct, "?")} %" + (d.UptimeSeconds is { } up ? $", up {Duration(up)}." : "."));
        if (d.Sensors.Count > 0) a.Line("- Temperatures: " + string.Join(", ", d.Sensors.Select(t => $"{t.Name} {Num(t.C, "?")} °C")) + ".");
        else if (d.TemperatureC is { } general) a.Line($"- Temperature {Num(general)} °C.");
        if (d.Overheating) a.Line("- **Overheating.**");
        if (d.HasFan) a.Line($"- Fan level {d.FanLevel?.ToString(CultureInfo.InvariantCulture) ?? "?"}.");
        if (d.PoeBudgetW is { } budget) a.Line($"- PoE: {Num(d.PoeUsedW, "?")} W used of {Num(budget)} W.");
        if (d.Uplink is { } u)
            a.Line($"- Uplink: {u.Type} " + (u.DeviceName is { } upName ? $"to {upName} port {u.RemotePort ?? "?"}" : $"on {u.Interface ?? "?"}")
                   + $", {u.SpeedMbps?.ToString(CultureInfo.InvariantCulture) ?? "?"} Mbps" + (u.FullDuplex ? " full duplex" : "") + $", errors rx {u.RxErrors} / tx {u.TxErrors}.");
        foreach (var s in d.Storage) a.Line($"- Storage {s.Name}: {(s.UsedPct is { } pct ? Math.Round(pct).ToString(CultureInfo.InvariantCulture) : "?")} % used.");
        a.Line();

        if (d.Radios.Count > 0)
            a.Line("## Radios").Line(Answer.Table(["Band", "Channel", "Width (MHz)", "Tx power", "Clients", "Channel use %", "Interference %", "Retries %"],
                d.Radios.Select(r => new[] { Band(r.Radio), Num(r.Channel), Num(r.WidthMhz), Num(r.TxPower), Num(r.Clients), Num(r.ChannelUsePct), Num(r.InterferencePct), Num(r.RetriesPct) })));

        if (d.Ports.Count == 0) return;
        a.Line($"## Ports: {d.Ports.Count(p => p.State == "up")} up of {d.Ports.Count}");
        a.Line(Answer.Table(["Port", "Name", "Connected", "Link", "Speed (Mbps)", "Max (Mbps)", "Duplex", "PoE W", "Rx errors", "Tx errors", "Drops", "Link downs", "STP", "Flags"],
            d.Ports.Where(p => p.State == "up" || p.Flags.Count > 0).Select(p => new[]
            {
                Num(p.Index), p.Name, ConnectedCell(p), p.State, Num(p.SpeedMbps), Num(p.MaxSpeedMbps), p.Duplex ?? "", Num(p.PoeW),
                Num(p.RxErrors), Num(p.TxErrors), Num(p.Drops), Num(p.LinkDowns), p.Stp ?? "", string.Join(", ", p.Flags),
            })));
        var idle = d.Ports.Where(p => p.State != "up" && p.Flags.Count == 0).Select(p => Num(p.Index)).ToList();
        if (idle.Count > 0) a.Line($"Down with nothing to report: {string.Join(", ", idle)}.");
    }

    private static string Num(double? value, string none = "") => value?.ToString(CultureInfo.InvariantCulture) ?? none;

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
}
