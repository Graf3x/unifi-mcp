using System.Globalization;
using System.Text.Json;
using static UnifiMcp.ConsoleJson;

namespace UnifiMcp;

/// <summary>What one port can do, from the official API: its top speed (null when unknown) and its connector.</summary>
internal sealed record PortCapability(long? MaxSpeedMbps, string Connector);

/// <summary>
/// The device reading: what the server makes of one device record from the console's internal API. Every rule about a
/// device lives here (whether it is online, what its state is called, its hottest sensor, which ports are flagged and
/// why, what is plugged into each port), and the text answer and the view both render what it returns. It reads
/// nothing itself: the caller passes what it has read, and an empty list for a read that failed.
/// </summary>
internal static class DeviceReading
{
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal) { "RJ45", "SFP", "SFPPLUS", "SFP28", "QSFP28" };

    /// <summary>The console reports the device as connected. Every other state counts as not online.</summary>
    public static bool IsOnline(JsonElement device) => Text(device, "state") == "1";

    /// <summary>The device's state in words: online, offline, upgrading, …</summary>
    public static string StateName(JsonElement device) => Text(device, "state") switch
    {
        "0" => "offline", "1" => "online", "2" => "pending adoption", "4" => "upgrading", "5" => "provisioning",
        "6" => "heartbeat missed", "7" => "adopting", "9" => "adoption failed", "10" or "11" => "isolated", var s => $"state {s}",
    };

    /// <summary>One device at a glance, for the summary.</summary>
    public static DeviceTile Tile(JsonElement d) => new(
        Text(d, "name"), Text(d, "mac"), Text(d, "model"), Text(d, "type"), StateName(d), Uptime(d), Stat(d, "cpu"), Stat(d, "mem"),
        Hottest(d), Text(d, "version"), UpdateTo(d), Issues(d).ToList());

    /// <summary>
    /// One device in full, with every port. <paramref name="devices"/> is the site's device list (who is plugged into
    /// whom), <paramref name="stations"/> the site's clients, and <paramref name="capabilities"/> what each port can do,
    /// by port number.
    /// </summary>
    public static DeviceDetail Detail(JsonElement d, IReadOnlyList<JsonElement> devices, IReadOnlyList<JsonElement> stations,
        IReadOnlyDictionary<string, PortCapability> capabilities)
    {
        var ports = Items(d, "port_table");
        var connected = ConnectedDevices(devices, d);
        if (Upstream(devices, d, ports) is { } upstream) Add(connected, upstream.Port, upstream.Device);
        foreach (var c in stations.Where(c => Bool(c, "is_wired") && SameMac(Text(c, "sw_mac"), Text(d, "mac")) && Text(c, "sw_port").Length > 0).OrderBy(ClientName))
            Add(connected, Text(c, "sw_port"), new Connected("client", ClientName(c), Text(c, "mac")));

        var poe = Dbl(d, "total_max_power") is > 0;
        return new(Text(d, "name"), Text(d, "mac"), Text(d, "model"), Text(d, "type"), StateName(d), Text(d, "ip"), Text(d, "version"), UpdateTo(d),
            Uptime(d), Stat(d, "cpu"), Stat(d, "mem"), Hottest(d), Bool(d, "overheating"),
            poe ? Dbl(d, "total_used_power") : null, poe ? Dbl(d, "total_max_power") : null,
            Uplink(d),
            ports.Select(p =>
            {
                var idx = Text(p, "port_idx");
                var cap = capabilities.TryGetValue(idx, out var c) ? c : new PortCapability(null, "RJ45");
                return Port(p, connected.TryGetValue(idx, out var on) ? on : [], cap);
            }).ToList(),
            Items(d, "temperatures").Select(t => new Sensor(Text(t, "name"), Dbl(t, "value"))).ToList(),
            Bool(d, "has_fan"), Long(d, "fan_level"),
            Items(d, "storage").Select(s => new StorageUse(Text(s, "name"), Dbl(s, "used") is { } used && Dbl(s, "size") is > 0 and var size ? 100 * used / size : null)).ToList(),
            Items(d, "radio_table_stats").Select(r => new RadioFacts(Text(r, "radio"), Long(r, "channel"), Long(r, "bw"), Dbl(r, "tx_power"), Long(r, "num_sta"),
                Dbl(r, "cu_total"), Dbl(r, "cu_interf"), Dbl(r, "tx_retries_pct"))).ToList());
    }

    /// <summary>The version an update brings when the console does not say which.</summary>
    public const string UnknownVersion = "available";

    /// <summary>What each port can do, by port number, from the official API's device detail. An unknown connector reads as RJ45.</summary>
    public static Dictionary<string, PortCapability> Capabilities(JsonElement officialDetail)
    {
        var capabilities = new Dictionary<string, PortCapability>(StringComparer.Ordinal);
        foreach (var p in Items(Obj(officialDetail, "interfaces"), "ports"))
            capabilities[Text(p, "idx")] = new(Long(p, "maxSpeedMbps"), Connectors.Contains(Text(p, "connector")) ? Text(p, "connector") : "RJ45");
        return capabilities;
    }

    /// <summary>MAC addresses compare by their hex digits: the console writes them lower case, the official API upper.</summary>
    public static bool SameMac(string a, string b)
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

    // --- ports -------------------------------------------------------------------------------------------------------

    private static PortView Port(JsonElement p, List<Connected> connected, PortCapability cap)
    {
        var up = Bool(p, "up");
        long? speed = up && Long(p, "speed") is > 0 and var s ? s : null;
        var flags = PortFlags(p, cap.MaxSpeedMbps);
        long rx = Long(p, "rx_errors") ?? 0, tx = Long(p, "tx_errors") ?? 0, downs = Long(p, "link_down_count") ?? 0;
        return new((int)(Long(p, "port_idx") ?? 0), Text(p, "name"), up ? "up" : Bool(p, "enable") ? "down" : "disabled", speed, cap.MaxSpeedMbps, cap.Connector,
            up ? (Bool(p, "full_duplex") ? "full" : "half") : null, Bool(p, "poe_enable") ? Dbl(p, "poe_power") : null, rx, tx,
            (long)((Dbl(p, "rx_dropped") ?? 0) + (Dbl(p, "tx_dropped") ?? 0)), downs, NullIfEmpty(Text(p, "stp_state")), flags,
            PortNote(flags, speed, cap.MaxSpeedMbps, rx, tx, downs), connected.Take(5).ToList(), connected.Count);
    }

    /// <summary>
    /// The one place a port's flags are decided. A port at 100 Mb or less is a slow link unless that is all the port can
    /// do (<paramref name="maxSpeedMbps"/>, null when unknown).
    /// </summary>
    private static List<string> PortFlags(JsonElement p, long? maxSpeedMbps)
    {
        var flags = new List<string>();
        if ((Dbl(p, "rx_errors") ?? 0) + (Dbl(p, "tx_errors") ?? 0) > 0) flags.Add("errors");
        if ((Dbl(p, "link_down_count") ?? 0) >= 5) flags.Add("flapping");
        if (Bool(p, "up") && !Bool(p, "full_duplex")) flags.Add("half duplex");
        if (Bool(p, "up") && Dbl(p, "speed") is > 0 and <= 100 && maxSpeedMbps is not <= 100) flags.Add("slow link");
        return flags;
    }

    /// <summary>
    /// The UniFi devices plugged into this one, by its port number. Every device names its parent and the parent's port
    /// in its own uplink.
    /// </summary>
    private static Dictionary<string, List<Connected>> ConnectedDevices(IReadOnlyList<JsonElement> devices, JsonElement parent)
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
    private static (string Port, Connected Device)? Upstream(IReadOnlyList<JsonElement> devices, JsonElement dev, List<JsonElement> ports)
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

    // --- the device --------------------------------------------------------------------------------------------------

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

    private static UplinkFacts? Uplink(JsonElement d) =>
        Obj(d, "uplink") is { } up && Text(up, "type").Length > 0
            ? new UplinkFacts(Text(up, "type"), NullIfEmpty(Text(up, "uplink_device_name")), NullIfEmpty(Text(up, "uplink_remote_port")), Long(up, "speed"),
                Bool(up, "full_duplex"), Long(up, "rx_errors") ?? 0, Long(up, "tx_errors") ?? 0, NullIfEmpty(First(Text(up, "comment"), Text(up, "name"))))
            : null;

    private static long? Uptime(JsonElement d) => IsOnline(d) ? Long(d, "uptime") : null;

    private static double? Stat(JsonElement d, string name) => Obj(d, "system-stats") is { } s ? Dbl(s, name) : null;

    private static string? UpdateTo(JsonElement d) => Bool(d, "upgradable") ? First(Text(d, "upgrade_to_firmware"), UnknownVersion) : null;

    private static string First(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
