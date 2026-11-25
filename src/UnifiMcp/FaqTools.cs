using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static UnifiMcp.ConsoleJson;

namespace UnifiMcp;

/// <summary>
/// Task-oriented composites in the style of Data-Manager's <c>*_faq</c> tools: named steps in markdown, each step
/// protected so one failure is reported and the rest still run. They state plainly what this API version cannot show.
/// </summary>
[McpServerToolType]
public sealed class FaqTools(UnifiClient console, ConsoleReads reads)
{
    /// <summary>A network from the list; <see cref="Details"/> is null when its details could not be read, so its subnet is unknown.</summary>
    private sealed record Net(string Name, int Vlan, IPAddress? Host, int Prefix, JsonElement? Details);

    /// <summary>
    /// What is known of the site's networks. <see cref="All"/> is null when the list could not be read, and
    /// <see cref="Listed"/> is false when it was cut short, so a network may be missing from it. A client in no subnet is
    /// outside every network only when <see cref="Complete"/>; otherwise its network is unknown.
    /// </summary>
    private sealed record Networks(List<Net>? All, bool Listed)
    {
        public List<Net> Read => All?.Where(n => n.Details is not null).ToList() ?? [];
        public List<string> Unread => All?.Where(n => n.Details is null).Select(n => n.Name).ToList() ?? [];
        public bool Complete => All is not null && Listed && Unread.Count == 0;

        /// <summary>Why a client in no subnet that was read may still be in one.</summary>
        public string Gap
        {
            get
            {
                if (All is null) return "the list of networks could not be read";
                var gaps = new List<string>();
                if (!Listed) gaps.Add("the console has more networks than were listed");
                if (Unread is { Count: > 0 } unread) gaps.Add($"the details of these networks could not be read: {string.Join(", ", unread)}");
                return string.Join("; ", gaps);
            }
        }
    }

    [McpServerTool(Name = "who_is_on_network_faq", Title = "Who is on this network", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Who is on a network: connected clients grouped by network (matched by IPv4 subnet) with IP, MAC, type and the device they connect through. Omit network for all of them.")]
    public Task<CallToolResult> WhoIsOnNetwork(
        [Description("Network name (partial match) or VLAN number. Omit for every network.")] string? network = null,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var id = (await console.FindSiteAsync(siteId, ct)).Id;
        // What the answer needs comes first, within the answer's budget: the clients, then only the networks asked for,
        // and the device names (which only label the table) last.
        var clients = await a.Step("Connected clients", () => console.GetAllAsync($"/v1/sites/{id}/clients", ct));
        if (clients is null) return;
        var nets = await NetworksAsync(a, id, ct, (name, vlan) =>
            network is null || name.Contains(network, StringComparison.OrdinalIgnoreCase) || vlan.ToString() == network.Trim());
        var devices = await a.Step("Devices", () => DevicesAsync(id, ct)) ?? new();

        if (nets.All is null && network is not null)
        {
            a.Line($"Who is on '{network}' could not be checked: {nets.Gap}.");
            return;
        }
        var shown = nets.All ?? [];
        if (network is not null && shown.Count == 0)
            a.Line(nets.Listed ? $"No network matches '{network}'." : $"No network listed matches '{network}', but {nets.Gap}, so it may be one of the others.").Line();
        foreach (var n in shown)
        {
            if (n.Details is null)
            {
                a.Line($"## {n.Name} (VLAN {n.Vlan}): subnet unknown").Line("Its details could not be read, so which clients are on it could not be checked.").Line();
                continue;
            }
            var members = clients.Where(c => InSubnet(Ip(c), n)).ToList();
            a.Line($"## {n.Name} (VLAN {n.Vlan}, {n.Host}/{n.Prefix}): {members.Count} client(s)");
            a.Line(ClientTable(members, devices));
        }

        if (network is not null) return;
        var read = nets.Read;
        var unmatched = clients.Where(c => !read.Any(n => InSubnet(Ip(c), n))).ToList();
        if (unmatched.Count == 0) return;
        a.Line(nets.Complete
            ? "## No matching network (VPN, or outside every known subnet)"
            : $"## Network unknown (in no subnet that could be read; {nets.Gap})").Line(ClientTable(unmatched, devices));
    });

    [McpServerTool(Name = "client_path_faq", Title = "Client network path", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] What path a client takes: finds the client by name, IP or MAC and walks its uplinks device by device to the gateway, then names the switch and port it is plugged into (or its AP, on Wi-Fi).")]
    public Task<CallToolResult> ClientPath(
        [Description("Client name (partial match), IP address or MAC address.")] string client,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var id = (await console.FindSiteAsync(siteId, ct)).Id;
        var matches = await FindClientsAsync(id, client, ct, a);
        if (matches.Count != 1) return;
        var c = matches[0];
        a.Line($"## Client\n{TextOrNull(c, "name")} ({TextOrNull(c, "type")}), IP {Ip(c) ?? "?"}, MAC {TextOrNull(c, "macAddress") ?? "?"}").Line();
        if (TextOrNull(c, "uplinkDeviceId") is not { } device)
        {
            a.Line("This client has no uplink device (VPN or Teleport connection).");
            return;
        }

        a.Line("## Path to the gateway");
        var hops = new List<JsonElement>();
        var seen = new HashSet<string>();
        for (var next = device; next is not null && seen.Add(next) && hops.Count < 10;)
        {
            var hop = await a.Step($"Device {next}", () => console.GetJsonAsync($"/v1/sites/{id}/devices/{next}", ct));
            if (hop is not { } d) break;
            hops.Add(d);
            next = d.TryGetProperty("uplink", out var u) ? TextOrNull(u, "deviceId") : null;
        }
        a.Line(Answer.Table(["Hop", "Device", "Model", "State", "IP"],
            hops.Select((d, i) => new[] { (i + 1).ToString(), TextOrNull(d, "name") ?? "?", TextOrNull(d, "model") ?? "?", TextOrNull(d, "state") ?? "?", TextOrNull(d, "ipAddress") ?? "" })));
        if (await StationAsync(a, siteId, TextOrNull(c, "macAddress"), ct) is { } sta)
        {
            a.Line(Text(sta, "is_wired") == "true"
                ? $"**Which port:** plugged into {Text(sta, "last_uplink_name")} port {Text(sta, "sw_port")} at {Text(sta, "wired_rate_mbps")} Mbps."
                : $"**Which port:** none, it is on Wi-Fi '{Text(sta, "essid")}' via {Text(sta, "last_uplink_name")} (client_diagnostics_faq shows its signal).");
            return;
        }
        a.Line("**Which port:** the console did not report the switch port for this client, only the device it uplinks through.");
        if (hops.Count > 0 && hops[0].TryGetProperty("interfaces", out var ifs) && ifs.TryGetProperty("ports", out var ports))
        {
            a.Line().Line($"Ports that are up on {TextOrNull(hops[0], "name")}:");
            a.Line(Answer.Table(["Port", "State", "Speed (Mbps)"], ports.EnumerateArray().Where(p => TextOrNull(p, "state") == "UP")
                .Select(p => new[] { NumberText(p, "idx"), "UP", NumberText(p, "speedMbps") })));
        }
    });

    [McpServerTool(Name = "dhcp_reservation_faq", Title = "Is a DHCP reservation honoured", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Is a DHCP reservation honoured: compares a client's current IP with the address you expected and shows whether it sits inside the network's DHCP pool, and reads the client's fixed-IP reservation from the console. Without expected_ip it checks the address against the reservation.")]
    public Task<CallToolResult> DhcpReservation(
        [Description("Client name (partial match), IP address or MAC address.")] string client,
        [Description("The address the reservation should give it.")] string? expected_ip = null,
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var id = (await console.FindSiteAsync(siteId, ct)).Id;
        var matches = await FindClientsAsync(id, client, ct, a);
        if (matches.Count != 1) return;
        var ip = Ip(matches[0]);
        a.Line($"## Client\n{TextOrNull(matches[0], "name")}: current IP {ip ?? "unknown"}, MAC {TextOrNull(matches[0], "macAddress") ?? "?"}").Line();

        var nets = await NetworksAsync(a, id, ct);
        var net = nets.Read.FirstOrDefault(n => InSubnet(ip, n));
        a.Line("## Network");
        if (net is not null) a.Line(DescribePool(net, ip)).Line();
        else if (nets.Complete) a.Line("The client's address is in no known subnet.").Line();
        else a.Line($"Which network the client's address is in is unknown, so its DHCP pool was not checked: it is in no subnet that could be read, and {nets.Gap}.").Line();

        var sta = await StationAsync(a, siteId, TextOrNull(matches[0], "macAddress"), ct);
        var reserved = sta is { } s && Text(s, "use_fixedip") == "true" ? Text(s, "fixed_ip") : null;
        a.Line("## Reservation");
        a.Line(sta is null ? "The console did not report this client's reservation, so whether one exists has to be checked in the UniFi app (the client's Settings, Fixed IP)."
            : reserved is null ? "No fixed IP reservation is set for this client." : $"Fixed IP reservation: {reserved}.").Line();

        a.Line("## Verdict");
        var expected = expected_ip?.Trim() ?? reserved;
        if (expected is not null)
            a.Line(string.Equals(ip, expected, StringComparison.Ordinal)
                ? $"The client's current address matches the expected {expected}."
                : $"**NOT honoured:** the client holds {ip ?? "no address"}, expected {expected}. It may need to renew its lease.");
        if (expected_ip is not null && reserved is not null && expected_ip.Trim() != reserved)
            a.Line($"Note: the reservation itself is {reserved}, not {expected_ip}.");
    });

    [McpServerTool(Name = "network_health_faq", Title = "Network health summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Composite] Network health at a glance: console version, every device's state with offline devices and pending firmware called out, and connected client counts by type.")]
    public Task<CallToolResult> NetworkHealth(
        [Description("Site UUID or name. Omit for the configured site.")] string? siteId = null,
        CancellationToken ct = default) => Answer.RunAsync(async a =>
    {
        var id = (await console.FindSiteAsync(siteId, ct)).Id;
        var info = await a.Step("Console", () => console.GetJsonAsync("/v1/info", ct));
        if (info is { } i) a.Line($"Application version {TextOrNull(i, "applicationVersion")}.").Line();

        var devices = await a.Step("Devices", () => console.GetAllAsync($"/v1/sites/{id}/devices", ct));
        if (devices is not null)
        {
            var offline = devices.Where(d => TextOrNull(d, "state") != "ONLINE").ToList();
            a.Line($"## Devices: {devices.Count}, {offline.Count} not online");
            a.Line(Answer.Table(["Device", "Model", "State", "IP", "Firmware update"], devices.Select(d => new[]
            {
                TextOrNull(d, "name") ?? "?", TextOrNull(d, "model") ?? "?", TextOrNull(d, "state") ?? "?", TextOrNull(d, "ipAddress") ?? "",
                d.TryGetProperty("firmwareUpdatable", out var f) && f.ValueKind == JsonValueKind.True ? "available" : "",
            })));
        }

        var clients = await a.Step("Clients", () => console.GetAllAsync($"/v1/sites/{id}/clients", ct));
        if (clients is not null)
            a.Line($"## Clients: {clients.Count}").Line(string.Join(", ", clients.GroupBy(c => TextOrNull(c, "type") ?? "?").OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}")));
    });

    private async Task<List<JsonElement>> FindClientsAsync(string site, string query, CancellationToken ct, Answer a)
    {
        var q = query.Trim();
        var matches = (await console.GetAllAsync($"/v1/sites/{site}/clients", ct)).Where(c =>
            string.Equals(TextOrNull(c, "id"), q, StringComparison.OrdinalIgnoreCase)
            || string.Equals(TextOrNull(c, "macAddress"), q, StringComparison.OrdinalIgnoreCase)
            || Ip(c) == q
            || (TextOrNull(c, "name") ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) a.Line($"No connected client matches '{query}'. The API lists connected clients only.");
        else if (matches.Count > 1)
            a.Line($"'{query}' matches {matches.Count} clients; use the IP or MAC:").Line(Answer.Table(["Name", "IP", "MAC"], matches.Select(c => new[] { TextOrNull(c, "name") ?? "?", Ip(c) ?? "", TextOrNull(c, "macAddress") ?? "" })));
        return matches;
    }

    /// <summary>
    /// The console's own record of a connected client (switch port, fixed IP), or null when it has none. The internal API
    /// is optional here, so reading it is a step: when it fails the answer says why, then what it could not check.
    /// </summary>
    private async Task<JsonElement?> StationAsync(Answer a, string? site, string? mac, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var stations = await a.Step("Console client record", async () => await (await reads.SiteAsync(site, ct)).StationsAsync(ct));
        foreach (var s in stations ?? [])
            if (string.Equals(Text(s, "mac"), mac, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>
    /// The site's networks, the list and then the details of each one <paramref name="wanted"/> keeps (every one when
    /// null), every read a step. A network whose details fail is kept with its subnet unknown, and the others are still read.
    /// </summary>
    private async Task<Networks> NetworksAsync(Answer a, string site, CancellationToken ct, Func<string, int, bool>? wanted = null)
    {
        var list = await a.Step("Networks", () => console.GetListAsync($"/v1/sites/{site}/networks", ct));
        if (list is null) return new Networks(null, false);
        var result = new List<Net>();
        var stopped = false;
        foreach (var n in list.Items)
        {
            var name = TextOrNull(n, "name") ?? "?";
            var vlan = n.ValueKind == JsonValueKind.Object && n.TryGetProperty("vlanId", out var vl) && vl.TryGetInt32(out var v) ? v : 0;
            if (wanted is not null && !wanted(name, vlan)) continue;
            if (stopped)
            {
                result.Add(new Net(name, vlan, null, 0, null));
                continue;
            }
            var net = await a.Step($"Network {name}", async () =>
            {
                var d = await console.GetJsonAsync($"/v1/sites/{site}/networks/{TextOrNull(n, "id")}", ct);
                IPAddress? host = null;
                var prefix = 0;
                if (d.TryGetProperty("ipv4Configuration", out var v4) && v4.ValueKind == JsonValueKind.Object)
                {
                    if (IPAddress.TryParse(TextOrNull(v4, "hostIpAddress"), out var parsed)) host = parsed;
                    if (v4.TryGetProperty("prefixLength", out var p) && p.TryGetInt32(out var bits)) prefix = bits;
                }
                return new Net(name, vlan, host, prefix, d);
            });
            // Once the answer's console budget is spent every further read would fail alike, so the rest stay unread
            // (subnet unknown) with one failure line, not one each.
            stopped = net is null && UpstreamBudget.Current?.Exhausted == true;
            result.Add(net ?? new Net(name, vlan, null, 0, null));
        }
        return new Networks(result, list.Complete);
    }

    private async Task<Dictionary<string, string>> DevicesAsync(string site, CancellationToken ct) =>
        (await console.GetAllAsync($"/v1/sites/{site}/devices", ct)).ToDictionary(d => TextOrNull(d, "id") ?? "", d => TextOrNull(d, "name") ?? "?");

    private static string DescribePool(Net net, string? ip)
    {
        var header = $"{net.Name} (VLAN {net.Vlan}, {net.Host}/{net.Prefix}). ";
        if (net.Details is not { } details || !details.TryGetProperty("ipv4Configuration", out var v4) || !v4.TryGetProperty("dhcpConfiguration", out var dhcp) || dhcp.ValueKind != JsonValueKind.Object)
            return header + "No DHCP on this network.";
        if (TextOrNull(dhcp, "mode") != "SERVER" || !dhcp.TryGetProperty("ipAddressRange", out var range))
            return header + $"DHCP mode {TextOrNull(dhcp, "mode")}: addresses come from elsewhere.";
        var start = TextOrNull(range, "start");
        var stop = TextOrNull(range, "stop");
        var inside = ToUInt(ip) is { } a && ToUInt(start) is { } s && ToUInt(stop) is { } e && a >= s && a <= e;
        return header + $"DHCP server pool {start} to {stop}. The client's address is {(inside ? "inside" : "outside")} the pool.";
    }

    private static string ClientTable(List<JsonElement> clients, Dictionary<string, string> devices) =>
        Answer.Table(["Client", "IP", "MAC", "Type", "Via"], clients.Select(c =>
        {
            var via = TextOrNull(c, "uplinkDeviceId");
            return new[] { TextOrNull(c, "name") ?? "?", Ip(c) ?? "", TextOrNull(c, "macAddress") ?? "", TextOrNull(c, "type") ?? "?", via is null ? "" : devices.GetValueOrDefault(via, via) };
        }));

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

    private static string? Ip(JsonElement c) => TextOrNull(c, "ipAddress");
}
