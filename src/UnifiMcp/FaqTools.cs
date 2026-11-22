using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>
/// Task-oriented composites in the style of Data-Manager's <c>*_faq</c> tools: named steps in markdown, each step
/// protected so one failure is reported and the rest still run. They state plainly what this API version cannot show.
/// </summary>
[McpServerToolType]
public sealed class FaqTools(UnifiClient console, ConsoleReads reads)
{
    private sealed record Net(string Name, int Vlan, IPAddress? Host, int Prefix, JsonElement Details);

    [McpServerTool(Name = "who_is_on_network_faq", Title = "Who is on this network", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Who is on a network: connected clients grouped by network (matched by IPv4 subnet) with IP, MAC, type and the device they connect through. Omit network for all of them.")]
    public Task<string> WhoIsOnNetwork(
        [Description("Network name (partial match) or VLAN number. Omit for every network.")] string? network = null,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Run(async (id, sb) =>
    {
        var nets = await Step(sb, "Networks", () => NetworksAsync(id, ct)) ?? [];
        var devices = await Step(sb, "Devices", () => DevicesAsync(id, ct)) ?? new();
        var clients = await Step(sb, "Connected clients", () => console.GetAllAsync($"/v1/sites/{id}/clients", ct));
        if (clients is null) return;

        var shown = nets.Where(n => network is null || n.Name.Contains(network, StringComparison.OrdinalIgnoreCase) || n.Vlan.ToString() == network.Trim()).ToList();
        if (network is not null && shown.Count == 0) sb.AppendLine($"No network matches '{network}'.").AppendLine();
        foreach (var n in shown)
        {
            var members = clients.Where(c => InSubnet(Ip(c), n)).ToList();
            sb.AppendLine($"## {n.Name} (VLAN {n.Vlan}, {n.Host}/{n.Prefix}): {members.Count} client(s)");
            sb.AppendLine(ClientTable(members, devices));
        }

        if (network is not null) return;
        var unmatched = clients.Where(c => !nets.Any(n => InSubnet(Ip(c), n))).ToList();
        if (unmatched.Count > 0) sb.AppendLine("## No matching network (VPN, or outside every known subnet)").AppendLine(ClientTable(unmatched, devices));
    }, siteId, ct);

    [McpServerTool(Name = "client_path_faq", Title = "Client network path", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] What path a client takes: finds the client by name, IP or MAC and walks its uplinks device by device to the gateway, then names the switch and port it is plugged into (or its AP, on Wi-Fi).")]
    public Task<string> ClientPath(
        [Description("Client name (partial match), IP address or MAC address.")] string client,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Run(async (id, sb) =>
    {
        var matches = await FindClientsAsync(id, client, ct, sb);
        if (matches.Count != 1) return;
        var c = matches[0];
        sb.AppendLine($"## Client\n{Str(c, "name")} ({Str(c, "type")}), IP {Ip(c) ?? "?"}, MAC {Str(c, "macAddress") ?? "?"}").AppendLine();
        if (Str(c, "uplinkDeviceId") is not { } device)
        {
            sb.AppendLine("This client has no uplink device (VPN or Teleport connection).");
            return;
        }

        sb.AppendLine("## Path to the gateway");
        var hops = new List<JsonElement>();
        var seen = new HashSet<string>();
        for (var next = device; next is not null && seen.Add(next) && hops.Count < 10;)
        {
            var hop = await StepJson(sb, $"Device {next}", () => console.GetJsonAsync($"/v1/sites/{id}/devices/{next}", ct));
            if (hop is not { } d) break;
            hops.Add(d);
            next = d.TryGetProperty("uplink", out var u) ? Str(u, "deviceId") : null;
        }
        sb.AppendLine(Table(["Hop", "Device", "Model", "State", "IP"],
            hops.Select((d, i) => new[] { (i + 1).ToString(), Str(d, "name") ?? "?", Str(d, "model") ?? "?", Str(d, "state") ?? "?", Str(d, "ipAddress") ?? "" })));
        if (await StationAsync(siteId, Str(c, "macAddress"), ct) is { } sta)
        {
            sb.AppendLine(DiagnosticTools.V(sta, "is_wired") == "true"
                ? $"**Which port:** plugged into {DiagnosticTools.V(sta, "last_uplink_name")} port {DiagnosticTools.V(sta, "sw_port")} at {DiagnosticTools.V(sta, "wired_rate_mbps")} Mbps."
                : $"**Which port:** none, it is on Wi-Fi '{DiagnosticTools.V(sta, "essid")}' via {DiagnosticTools.V(sta, "last_uplink_name")} (client_diagnostics_faq shows its signal).");
            return;
        }
        sb.AppendLine("**Which port:** the console did not report the switch port for this client, only the device it uplinks through.");
        if (hops.Count > 0 && hops[0].TryGetProperty("interfaces", out var ifs) && ifs.TryGetProperty("ports", out var ports))
        {
            sb.AppendLine().AppendLine($"Ports that are up on {Str(hops[0], "name")}:");
            sb.AppendLine(Table(["Port", "State", "Speed (Mbps)"], ports.EnumerateArray().Where(p => Str(p, "state") == "UP")
                .Select(p => new[] { Num(p, "idx"), "UP", Num(p, "speedMbps") })));
        }
    }, siteId, ct);

    [McpServerTool(Name = "dhcp_reservation_faq", Title = "Is a DHCP reservation honoured", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Is a DHCP reservation honoured: compares a client's current IP with the address you expected and shows whether it sits inside the network's DHCP pool, and reads the client's fixed-IP reservation from the console. Without expected_ip it checks the address against the reservation.")]
    public Task<string> DhcpReservation(
        [Description("Client name (partial match), IP address or MAC address.")] string client,
        [Description("The address the reservation should give it.")] string? expected_ip = null,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Run(async (id, sb) =>
    {
        var matches = await FindClientsAsync(id, client, ct, sb);
        if (matches.Count != 1) return;
        var ip = Ip(matches[0]);
        sb.AppendLine($"## Client\n{Str(matches[0], "name")}: current IP {ip ?? "unknown"}, MAC {Str(matches[0], "macAddress") ?? "?"}").AppendLine();

        var nets = await Step(sb, "Networks", () => NetworksAsync(id, ct)) ?? [];
        var net = nets.FirstOrDefault(n => InSubnet(ip, n));
        sb.AppendLine("## Network");
        if (net is null) sb.AppendLine("The client's address is in no known subnet.").AppendLine();
        else sb.AppendLine(DescribePool(net, ip)).AppendLine();

        var sta = await StationAsync(siteId, Str(matches[0], "macAddress"), ct);
        var reserved = sta is { } s && DiagnosticTools.V(s, "use_fixedip") == "true" ? DiagnosticTools.V(s, "fixed_ip") : null;
        sb.AppendLine("## Reservation");
        sb.AppendLine(sta is null ? "The console did not report this client's reservation, so whether one exists has to be checked in the UniFi app (the client's Settings, Fixed IP)."
            : reserved is null ? "No fixed IP reservation is set for this client." : $"Fixed IP reservation: {reserved}.").AppendLine();

        sb.AppendLine("## Verdict");
        var expected = expected_ip?.Trim() ?? reserved;
        if (expected is not null)
            sb.AppendLine(string.Equals(ip, expected, StringComparison.Ordinal)
                ? $"The client's current address matches the expected {expected}."
                : $"**NOT honoured:** the client holds {ip ?? "no address"}, expected {expected}. It may need to renew its lease.");
        if (expected_ip is not null && reserved is not null && expected_ip.Trim() != reserved)
            sb.AppendLine($"Note: the reservation itself is {reserved}, not {expected_ip}.");
    }, siteId, ct);

    [McpServerTool(Name = "network_health_faq", Title = "Network health summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Network health at a glance: console version, every device's state with offline devices and pending firmware called out, and connected client counts by type.")]
    public Task<string> NetworkHealth(
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Run(async (id, sb) =>
    {
        var info = await StepJson(sb, "Console", () => console.GetJsonAsync("/v1/info", ct));
        if (info is { } i) sb.AppendLine($"Application version {Str(i, "applicationVersion")}.").AppendLine();

        var devices = await Step(sb, "Devices", () => console.GetAllAsync($"/v1/sites/{id}/devices", ct));
        if (devices is not null)
        {
            var offline = devices.Where(d => Str(d, "state") != "ONLINE").ToList();
            sb.AppendLine($"## Devices: {devices.Count}, {offline.Count} not online");
            sb.AppendLine(Table(["Device", "Model", "State", "IP", "Firmware update"], devices.Select(d => new[]
            {
                Str(d, "name") ?? "?", Str(d, "model") ?? "?", Str(d, "state") ?? "?", Str(d, "ipAddress") ?? "",
                d.TryGetProperty("firmwareUpdatable", out var f) && f.ValueKind == JsonValueKind.True ? "available" : "",
            })));
        }

        var clients = await Step(sb, "Clients", () => console.GetAllAsync($"/v1/sites/{id}/clients", ct));
        if (clients is not null)
            sb.AppendLine($"## Clients: {clients.Count}").AppendLine(string.Join(", ", clients.GroupBy(c => Str(c, "type") ?? "?").OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}")));
    }, siteId, ct);

    /// <summary>Resolves the site and runs the steps; "not configured" and an unknown site end the tool early.</summary>
    private async Task<string> Run(Func<string, StringBuilder, Task> steps, string? site, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            await steps((await console.FindSiteAsync(site, ct)).Id, sb);
        }
        catch (Exception e) when (e is UnifiError or UnifiNotConfigured)
        {
            sb.AppendLine(e.Message);
        }
        return ToolText.Cap(sb.ToString());
    }

    private static async Task<T?> Step<T>(StringBuilder sb, string name, Func<Task<T>> run) where T : class
    {
        try { return await run(); }
        catch (UnifiError e)
        {
            sb.AppendLine($"**{name} step failed:** {e.Message}").AppendLine();
            return default;
        }
    }

    private static async Task<JsonElement?> StepJson(StringBuilder sb, string name, Func<Task<JsonElement>> run)
    {
        try { return await run(); }
        catch (UnifiError e)
        {
            sb.AppendLine($"**{name} step failed:** {e.Message}").AppendLine();
            return null;
        }
    }

    private async Task<List<JsonElement>> FindClientsAsync(string site, string query, CancellationToken ct, StringBuilder sb)
    {
        var q = query.Trim();
        var matches = (await console.GetAllAsync($"/v1/sites/{site}/clients", ct)).Where(c =>
            string.Equals(Str(c, "id"), q, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Str(c, "macAddress"), q, StringComparison.OrdinalIgnoreCase)
            || Ip(c) == q
            || (Str(c, "name") ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) sb.AppendLine($"No connected client matches '{query}'. The API lists connected clients only.");
        else if (matches.Count > 1)
            sb.AppendLine($"'{query}' matches {matches.Count} clients; use the IP or MAC:").AppendLine(Table(["Name", "IP", "MAC"], matches.Select(c => new[] { Str(c, "name") ?? "?", Ip(c) ?? "", Str(c, "macAddress") ?? "" })));
        return matches;
    }

    /// <summary>The console's own record of a connected client (switch port, fixed IP), or null when it cannot say.</summary>
    private async Task<JsonElement?> StationAsync(string? site, string? mac, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        try
        {
            foreach (var s in await (await reads.SiteAsync(site, ct)).StationsAsync(ct))
                if (string.Equals(DiagnosticTools.V(s, "mac"), mac, StringComparison.OrdinalIgnoreCase)) return s;
        }
        catch (UnifiError)
        {
            // The internal API is optional here: without it the answer says what it could not check.
        }
        return null;
    }

    private async Task<List<Net>> NetworksAsync(string site, CancellationToken ct)
    {
        var result = new List<Net>();
        foreach (var n in await console.GetAllAsync($"/v1/sites/{site}/networks", ct))
        {
            var d = await console.GetJsonAsync($"/v1/sites/{site}/networks/{Str(n, "id")}", ct);
            IPAddress? host = null;
            var prefix = 0;
            if (d.TryGetProperty("ipv4Configuration", out var v4) && v4.ValueKind == JsonValueKind.Object)
            {
                if (IPAddress.TryParse(Str(v4, "hostIpAddress"), out var parsed)) host = parsed;
                if (v4.TryGetProperty("prefixLength", out var p) && p.TryGetInt32(out var bits)) prefix = bits;
            }
            result.Add(new Net(Str(n, "name") ?? "?", n.TryGetProperty("vlanId", out var vlan) && vlan.TryGetInt32(out var v) ? v : 0, host, prefix, d));
        }
        return result;
    }

    private async Task<Dictionary<string, string>> DevicesAsync(string site, CancellationToken ct) =>
        (await console.GetAllAsync($"/v1/sites/{site}/devices", ct)).ToDictionary(d => Str(d, "id") ?? "", d => Str(d, "name") ?? "?");

    private static string DescribePool(Net net, string? ip)
    {
        var header = $"{net.Name} (VLAN {net.Vlan}, {net.Host}/{net.Prefix}). ";
        if (!net.Details.TryGetProperty("ipv4Configuration", out var v4) || !v4.TryGetProperty("dhcpConfiguration", out var dhcp) || dhcp.ValueKind != JsonValueKind.Object)
            return header + "No DHCP on this network.";
        if (Str(dhcp, "mode") != "SERVER" || !dhcp.TryGetProperty("ipAddressRange", out var range))
            return header + $"DHCP mode {Str(dhcp, "mode")}: addresses come from elsewhere.";
        var start = Str(range, "start");
        var stop = Str(range, "stop");
        var inside = ToUInt(ip) is { } a && ToUInt(start) is { } s && ToUInt(stop) is { } e && a >= s && a <= e;
        return header + $"DHCP server pool {start} to {stop}. The client's address is {(inside ? "inside" : "outside")} the pool.";
    }

    private static string ClientTable(List<JsonElement> clients, Dictionary<string, string> devices) =>
        Table(["Client", "IP", "MAC", "Type", "Via"], clients.Select(c =>
        {
            var via = Str(c, "uplinkDeviceId");
            return new[] { Str(c, "name") ?? "?", Ip(c) ?? "", Str(c, "macAddress") ?? "", Str(c, "type") ?? "?", via is null ? "" : devices.GetValueOrDefault(via, via) };
        }));

    internal static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder($"| {string.Join(" | ", headers)} |\n|{string.Concat(headers.Select(_ => "---|"))}\n");
        foreach (var row in rows) sb.AppendLine($"| {string.Join(" | ", row.Select(c => c.Replace("|", "\\|")))} |");
        return sb.ToString();
    }

    private static bool InSubnet(string? ip, Net net)
    {
        if (ToUInt(ip) is not { } a || net.Host is null || net.Prefix is < 1 or > 32 || ToUInt(net.Host.ToString()) is not { } h) return false;
        var mask = uint.MaxValue << (32 - net.Prefix);
        return (a & mask) == (h & mask);
    }

    private static uint? ToUInt(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var b = a.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    private static string? Ip(JsonElement c) => Str(c, "ipAddress");

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";
}
