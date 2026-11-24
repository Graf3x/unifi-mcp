using System.Text.Json;

namespace UnifiMcp.Tests;

/// <summary>
/// The device reading over hand-built console records: every rule about one device, without a host or a console. Made-up
/// names and addresses, in the shapes a Network 11.0 console returns.
/// </summary>
public class DeviceReadingTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<JsonElement> Js(params string[] json) => json.Select(J).ToList();

    private const string SwitchJson = """
        {"mac":"aa:00:00:00:00:03","type":"usw","model":"US24","name":"Core Switch","state":1,"ip":"192.168.1.2","version":"7.4.1",
         "uptime":172800,"system-stats":{"cpu":"3.0","mem":"55.0"},"general_temperature":61,"has_fan":true,"fan_level":0,
         "total_max_power":95,"total_used_power":21.5,"upgradable":true,"upgrade_to_firmware":"7.4.12",
         "uplink":{"type":"wire","port_idx":5,"uplink_device_name":"Gateway","uplink_remote_port":9,"speed":10000,"full_duplex":true,"rx_errors":0,"tx_errors":0},
         "port_table":[
           {"port_idx":1,"name":"Port 1","up":true,"enable":true,"speed":1000,"full_duplex":true,"poe_enable":true,"poe_power":"6.65","rx_errors":0,"tx_errors":0,"link_down_count":0,"stp_state":"forwarding"},
           {"port_idx":2,"name":"NAS","up":true,"enable":true,"speed":100,"full_duplex":false,"rx_errors":1532,"tx_errors":0,"rx_dropped":12,"tx_dropped":0,"link_down_count":14,"stp_state":"forwarding"},
           {"port_idx":3,"name":"Port 3","up":false,"enable":true,"speed":1000,"rx_errors":0,"tx_errors":0,"link_down_count":0},
           {"port_idx":4,"name":"Printer","up":true,"enable":true,"speed":100,"full_duplex":true,"rx_errors":0,"tx_errors":0,"link_down_count":0},
           {"port_idx":5,"name":"SFP 1","up":true,"enable":true,"speed":10000,"full_duplex":true,"rx_errors":0,"tx_errors":0,"link_down_count":0},
           {"port_idx":6,"name":"Spare","up":false,"enable":false,"rx_errors":0,"tx_errors":0,"link_down_count":0}]}
        """;

    private const string ApJson = """
        {"mac":"aa:00:00:00:00:01","type":"uap","model":"U6P","name":"Hall AP","state":1,"uptime":86400,
         "uplink":{"type":"wire","uplink_device_name":"Core Switch","uplink_mac":"aa:00:00:00:00:03","uplink_remote_port":1,"speed":1000}}
        """;

    private const string GatewayJson = """
        {"mac":"aa:00:00:00:00:04","type":"udm","model":"UDMPRO","name":"Gateway","state":1,"uptime":90000,
         "temperatures":[{"name":"CPU","value":45},{"name":"Local","value":43}],"port_table":[]}
        """;

    private static readonly JsonElement Switch = J(SwitchJson);

    private static List<JsonElement> Devices => Js(ApJson, SwitchJson, GatewayJson);

    private static readonly Dictionary<string, PortCapability> Capable = DeviceReading.Capabilities(J("""
        {"interfaces":{"ports":[{"idx":1,"connector":"RJ45","maxSpeedMbps":1000},{"idx":2,"connector":"RJ45","maxSpeedMbps":1000},
          {"idx":3,"connector":"RJ45","maxSpeedMbps":1000},{"idx":4,"connector":"RJ45","maxSpeedMbps":100},
          {"idx":5,"connector":"SFPPLUS","maxSpeedMbps":10000},{"idx":6,"connector":"RJ45","maxSpeedMbps":1000}]}}
        """));

    private static DeviceDetail Detail(JsonElement? device = null, List<JsonElement>? clients = null, Dictionary<string, PortCapability>? capabilities = null) =>
        DeviceReading.Detail(device ?? Switch, Devices, clients ?? [], capabilities ?? Capable);

    // --- online and state --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1", true)] [InlineData("0", false)] [InlineData("4", false)] [InlineData("6", false)]
    public void Only_a_connected_device_is_online(string state, bool online) =>
        Assert.Equal(online, DeviceReading.IsOnline(J($$"""{"state":{{state}}}""")));

    [Theory]
    [InlineData("0", "offline")] [InlineData("1", "online")] [InlineData("6", "heartbeat missed")] [InlineData("11", "isolated")] [InlineData("42", "state 42")]
    public void States_have_names(string state, string name) => Assert.Equal(name, DeviceReading.StateName(J($$"""{"state":{{state}}}""")));

    // --- the tile ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_tile_has_the_device_s_health_at_a_glance()
    {
        var t = DeviceReading.Tile(Switch);
        Assert.Equal(("Core Switch", "aa:00:00:00:00:03", "US24", "usw", "online"), (t.Name, t.Mac, t.Model, t.Type, t.State));
        Assert.Equal(172800, t.UptimeSeconds);
        Assert.Equal((3.0, 55.0), (t.CpuPct, t.MemoryPct));
        Assert.Equal(61, t.TemperatureC);
        Assert.Equal(("7.4.1", "7.4.12"), (t.Firmware, t.UpdateTo));
    }

    [Fact]
    public void An_update_without_a_version_is_still_an_update() =>
        Assert.Equal("available", DeviceReading.Tile(J("""{"state":1,"upgradable":true}""")).UpdateTo);

    [Fact]
    public void The_temperature_is_the_hottest_sensor_else_the_general_reading()
    {
        Assert.Equal(45, DeviceReading.Tile(J(GatewayJson)).TemperatureC);
        Assert.Equal(61, DeviceReading.Tile(Switch).TemperatureC);
        Assert.Null(DeviceReading.Tile(J(ApJson)).TemperatureC);
    }

    [Fact]
    public void An_offline_device_has_no_uptime() =>
        Assert.Null(DeviceReading.Tile(J("""{"state":0,"uptime":3600}""")).UptimeSeconds);

    [Fact]
    public void Issues_name_overheating_flagged_ports_and_uplink_errors()
    {
        var hot = J("""{"state":1,"overheating":true,"uplink":{"type":"wire","rx_errors":3},"port_table":[{"port_idx":7,"up":true,"full_duplex":true,"speed":1000,"rx_errors":2}]}""");
        Assert.Equal(["overheating", "port(s) 7 flagged", "uplink errors"], DeviceReading.Tile(hot).Issues);
        Assert.StartsWith("port(s) 2", DeviceReading.Tile(Switch).Issues[0]);
    }

    // --- the detail --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_detail_has_the_header_facts()
    {
        var d = Detail();
        Assert.Equal(("Core Switch", "192.168.1.2", "online"), (d.Name, d.Ip, d.State));
        Assert.Equal((21.5, 95.0), (d.PoeUsedW, d.PoeBudgetW));
        Assert.Equal(new UplinkFacts("wire", "Gateway", "9", 10000, FullDuplex: true), d.Uplink);
        Assert.False(d.Overheating);
    }

    [Fact]
    public void A_device_without_a_poe_budget_has_no_poe() => Assert.Null(Detail(J(GatewayJson)).PoeBudgetW);

    [Fact]
    public void Every_port_has_its_state_speed_and_capabilities()
    {
        var p = Detail().Ports;
        Assert.Equal([1, 2, 3, 4, 5, 6], p.Select(x => x.Index));
        Assert.Equal(["up", "up", "down", "up", "up", "disabled"], p.Select(x => x.State));
        Assert.Equal((100L, 1000L, "half"), (p[1].SpeedMbps!.Value, p[1].MaxSpeedMbps!.Value, p[1].Duplex));
        Assert.Null(p[2].SpeedMbps); // no link, no speed
        Assert.Null(p[2].Duplex);
        Assert.Equal("SFPPLUS", p[4].Connector);
        Assert.Equal(6.65, p[0].PoeW);
        Assert.Null(p[1].PoeW); // PoE is off on that port
        Assert.Equal((1532L, 0L, 12L, 14L, "forwarding"), (p[1].RxErrors, p[1].TxErrors, p[1].Drops, p[1].LinkDowns, p[1].Stp));
    }

    [Fact]
    public void A_port_is_flagged_for_errors_flapping_half_duplex_and_a_slow_link()
    {
        var p = Detail().Ports;
        Assert.Equal(["errors", "flapping", "half duplex", "slow link"], p[1].Flags);
        Assert.Equal("Linked at 100 Mb half duplex on a port that can do 1 GbE; receive errors; link dropped 14 times since the device restarted: suspect the cable or the device's network card.", p[1].Note);
        Assert.Empty(p[0].Flags);
        Assert.Null(p[0].Note);
    }

    [Fact]
    public void A_port_at_its_own_top_speed_is_not_a_slow_link()
    {
        Assert.Empty(Detail().Ports[3].Flags);
        Assert.Equal(["slow link"], Detail(capabilities: []).Ports[3].Flags); // top speed unknown
    }

    [Fact]
    public void A_port_with_unknown_capabilities_is_rj45_with_no_top_speed()
    {
        Assert.All(Detail(capabilities: []).Ports, p => Assert.Equal("RJ45", p.Connector));
        Assert.All(Detail(capabilities: []).Ports, p => Assert.Null(p.MaxSpeedMbps));
    }

    [Fact]
    public void Capabilities_keep_only_known_connectors() =>
        Assert.Equal(new PortCapability(2500, "RJ45"),
            DeviceReading.Capabilities(J("""{"interfaces":{"ports":[{"idx":1,"connector":"<b>","maxSpeedMbps":2500}]}}"""))["1"]);

    [Fact]
    public void The_detail_has_every_sensor_the_fan_and_the_storage()
    {
        var gw = Detail(J(GatewayJson.Replace("\"port_table\":[]", "\"storage\":[{\"name\":\"Backup\",\"size\":2000,\"used\":500},{\"name\":\"Odd\",\"size\":0,\"used\":1}],\"port_table\":[]")));
        Assert.Equal([new Sensor("CPU", 45), new Sensor("Local", 43)], gw.Sensors);
        Assert.Equal([new StorageUse("Backup", 25), new StorageUse("Odd", null)], gw.Storage);
        Assert.False(gw.HasFan);
        var sw = Detail();
        Assert.Empty(sw.Sensors);
        Assert.Equal(61, sw.TemperatureC);
        Assert.True(sw.HasFan);
        Assert.Equal(0, sw.FanLevel);
    }

    [Fact]
    public void The_uplink_has_its_duplex_and_errors()
    {
        var up = Detail().Uplink!;
        Assert.True(up.FullDuplex);
        Assert.Equal((0L, 0L), (up.RxErrors, up.TxErrors));
    }

    [Fact]
    public void An_uplink_to_no_known_device_names_its_own_interface()
    {
        var gw = J("""{"state":1,"uplink":{"type":"wire","name":"eth8","comment":"WAN","speed":1000,"rx_errors":4,"tx_errors":1}}""");
        var up = Detail(gw).Uplink!;
        Assert.Equal(("wire", null, "WAN"), (up.Type, up.DeviceName, up.Interface));
        Assert.Equal((4L, 1L, false), (up.RxErrors, up.TxErrors, up.FullDuplex));
        Assert.Equal("eth8", Detail(J("""{"uplink":{"type":"wire","name":"eth8"}}""")).Uplink!.Interface);
    }

    [Fact]
    public void An_access_point_has_its_radios()
    {
        var ap = J(ApJson.Replace("\"uptime\":86400,", "\"uptime\":86400,\"radio_table_stats\":[{\"radio\":\"na\",\"channel\":36,\"bw\":80,\"tx_power\":20,\"num_sta\":9,\"cu_total\":12,\"cu_interf\":3,\"tx_retries_pct\":4.1}],"));
        Assert.Equal([new RadioFacts("na", 36, 80, 20, 9, 12, 3, 4.1)], Detail(ap).Radios);
        Assert.Empty(Detail().Radios);
    }

    // --- what is plugged in where ------------------------------------------------------------------------------------

    [Fact]
    public void A_port_names_the_unifi_device_and_the_wired_clients_on_it()
    {
        var clients = Js("""{"mac":"cc:00:00:00:00:01","name":"nas","is_wired":true,"sw_mac":"AA:00:00:00:00:03","sw_port":2}""",
            """{"mac":"cc:00:00:00:00:02","name":"phone","is_wired":false,"sw_mac":"aa:00:00:00:00:03","sw_port":2}""",
            """{"mac":"cc:00:00:00:00:03","name":"elsewhere","is_wired":true,"sw_mac":"aa:00:00:00:00:09","sw_port":2}""");
        var p = Detail(clients: clients).Ports;
        Assert.Equal(new Connected("device", "Hall AP", "aa:00:00:00:00:01"), p[0].Connected.Single());
        Assert.Equal(new Connected("client", "nas", "cc:00:00:00:00:01"), p[1].Connected.Single());
    }

    [Fact]
    public void A_client_without_a_name_is_named_by_its_hostname_then_its_mac()
    {
        var clients = Js("""{"mac":"cc:00:00:00:00:01","hostname":"printer-host","is_wired":true,"sw_mac":"aa:00:00:00:00:03","sw_port":4}""",
            """{"mac":"cc:00:00:00:00:02","is_wired":true,"sw_mac":"aa:00:00:00:00:03","sw_port":4}""");
        Assert.Equal(["cc:00:00:00:00:02", "printer-host"], Detail(clients: clients).Ports[3].Connected.Select(c => c.Name));
    }

    [Fact]
    public void A_port_lists_five_connected_and_counts_the_rest()
    {
        var clients = Enumerable.Range(1, 7).Select(i => J($$"""{"mac":"cc:00:00:00:01:0{{i}}","name":"cam {{i}}","is_wired":true,"sw_mac":"aa:00:00:00:00:03","sw_port":4}""")).ToList();
        var port4 = Detail(clients: clients).Ports[3];
        Assert.Equal(5, port4.Connected.Count);
        Assert.Equal(7, port4.ConnectedCount);
    }

    [Fact]
    public void The_uplink_port_names_the_device_upstream()
    {
        var sfp = Detail().Ports[4];
        Assert.Equal(new Connected("device", "Gateway", "aa:00:00:00:00:04"), sfp.Connected.Single()); // found by name: this uplink carries no MAC
    }

    [Fact]
    public void The_uplink_port_is_found_by_the_console_mark_when_the_uplink_gives_no_port()
    {
        var marked = J(SwitchJson.Replace("\"type\":\"wire\",\"port_idx\":5,", "\"type\":\"wire\",")
            .Replace("{\"port_idx\":5,\"name\":\"SFP 1\",", "{\"port_idx\":5,\"is_uplink\":true,\"name\":\"SFP 1\","));
        Assert.Equal("Gateway", Detail(marked).Ports[4].Connected.Single().Name);
    }

    // --- words -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100L, "100 Mb")] [InlineData(1000L, "1 GbE")] [InlineData(2500L, "2.5 GbE")] [InlineData(10000L, "10 GbE")]
    public void Speeds_read_the_way_people_say_them(long mbps, string label) => Assert.Equal(label, DeviceReading.SpeedLabel(mbps));

    [Fact]
    public void A_slow_only_port_gets_a_note_about_the_device()
    {
        Assert.Equal("Linked at 100 Mb on a port that can do 1 GbE: the device or its cable may only support this speed.",
            DeviceReading.PortNote(["slow link"], 100, 1000, 0, 0, 0));
        Assert.Equal("Link dropped 6 times since the device restarted: check the cable and the device's power.",
            DeviceReading.PortNote(["flapping"], 1000, 1000, 0, 0, 6));
        Assert.Null(DeviceReading.PortNote([], 1000, 1000, 0, 0, 0));
    }
}
