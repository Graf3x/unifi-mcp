using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

/// <summary>
/// The diagnostics tools read the console's internal endpoints (the ones its own UI uses) with the same API key.
/// Fixtures follow the shapes a Network 11.0 console returns, with made-up values; secret-looking fields are seeded so
/// the tests can prove they never reach a tool's answer.
/// </summary>
public class DiagnosticTests
{
    private const string Classic = "/proxy/network/api/s/default";
    private const string V2 = "/proxy/network/v2/api/site/default";
    private const string MapsKey = "AIzaSy-maps-key-must-not-leak";
    private const string VwireKey = "vwire-key-must-not-leak";

    private static string Ok(params string[] items) => $$"""{"meta":{"rc":"ok"},"data":[{{string.Join(",", items)}}]}""";

    private const string SysInfo = $$"""{"timezone":"America/Los_Angeles","version":"11.0.83","google_maps_api_key":"{{MapsKey}}","sso_app_id":"sso-must-not-leak","hostname":"console"}""";

    private const string Ap = $$"""
        {"_id":"d1","mac":"aa:00:00:00:00:01","type":"uap","model":"U6P","name":"Hall AP","state":1,"ip":"192.168.1.20","version":"8.8.14",
         "x_vwirekey":"{{VwireKey}}","syslog_key":"syslog-must-not-leak","uptime":86400,"system-stats":{"cpu":"7.5","mem":"30.1","uptime":"86400"},
         "radio_table_stats":[
           {"name":"wifi0","radio":"ng","channel":6,"bw":20,"tx_power":12,"num_sta":4,"cu_total":71,"cu_interf":55,"tx_retries_pct":22.5,"satisfaction":80,"state":"RUN"},
           {"name":"wifi1","radio":"na","channel":36,"bw":80,"tx_power":20,"num_sta":9,"cu_total":12,"cu_interf":3,"tx_retries_pct":4.1,"satisfaction":99,"state":"RUN"}],
         "uplink":{"type":"wire","uplink_device_name":"Core Switch","uplink_remote_port":3,"speed":1000,"full_duplex":true,"rx_errors":0,"tx_errors":0} }
        """;

    private const string OfflineAp = """{"_id":"d2","mac":"aa:00:00:00:00:02","type":"uap","model":"U6M","name":"Garage AP","state":0,"radio_table_stats":[]}""";

    private const string Switch = """
        {"_id":"d3","mac":"aa:00:00:00:00:03","type":"usw","model":"US24","name":"Core Switch","state":1,"ip":"192.168.1.2","version":"7.4.1",
         "uptime":172800,"system-stats":{"cpu":"3.0","mem":"55.0","uptime":"172800"},"general_temperature":61,"overheating":false,"has_fan":true,"fan_level":0,
         "total_max_power":95,"total_used_power":21.5,"upgradable":true,"upgrade_to_firmware":"7.4.12",
         "uplink":{"type":"wire","uplink_device_name":"Gateway","uplink_remote_port":9,"speed":10000,"full_duplex":true,"rx_errors":0,"tx_errors":0},
         "port_table":[
           {"port_idx":1,"name":"Port 1","up":true,"enable":true,"speed":1000,"full_duplex":true,"poe_enable":true,"poe_power":"6.65","rx_errors":0,"tx_errors":0,"rx_dropped":0,"tx_dropped":0,"link_down_count":0,"stp_state":"forwarding","last_connection":{"mac":"aa:00:00:00:00:01"}},
           {"port_idx":2,"name":"NAS","up":true,"enable":true,"speed":100,"full_duplex":false,"poe_enable":false,"poe_power":"0.00","rx_errors":1532,"tx_errors":0,"rx_dropped":12,"tx_dropped":0,"link_down_count":14,"stp_state":"forwarding"},
           {"port_idx":3,"name":"Port 3","up":false,"enable":true,"speed":0,"rx_errors":0,"tx_errors":0,"link_down_count":0}]}
        """;

    private const string Gateway = """
        {"_id":"d4","mac":"aa:00:00:00:00:04","type":"udm","model":"UDMPRO","name":"Gateway","state":1,"ip":"192.168.1.1","version":"6.0.15",
         "uptime":90000,"system-stats":{"cpu":"53.2","mem":"72.3","uptime":"90000"},
         "temperatures":[{"name":"CPU","type":"cpu","value":45},{"name":"Local","type":"board","value":43}],"storage":[{"name":"Backup","size":2000,"used":500}],
         "port_table":[]}
        """;

    private const string WiredClient = """
        {"mac":"cc:00:00:00:00:01","name":"nas","hostname":"nas-host","oui":"Synology","ip":"192.168.1.50","is_wired":true,"network":"Main","last_uplink_name":"Core Switch",
         "sw_mac":"aa:00:00:00:00:03","sw_port":2,"wired_rate_mbps":100,"uptime":3600,"satisfaction":100,"use_fixedip":true,"fixed_ip":"192.168.1.50","last_seen":1794899590}
        """;

    private const string WirelessClient = """
        {"mac":"cc:00:00:00:00:02","name":"phone","hostname":"phone","oui":"Apple","ip":"192.168.1.71","is_wired":false,"network":"Main","last_uplink_name":"Hall AP",
         "ap_mac":"aa:00:00:00:00:01","essid":"Home","radio":"ng","channel":6,"channel_width":20,"signal":-79,"noise":-95,"rssi":16,"tx_rate":26000,"rx_rate":19500,
         "satisfaction":62,"wifi_tx_retries_percentage":31.5,"uptime":1200,"use_fixedip":false,"last_seen":1794899590}
        """;

    private const string OfflineClient = """
        {"mac":"cc:00:00:00:00:03","name":"Attic console","display_name":"Attic console","hostname":"CONSOLE1","type":"WIRED","status":"offline","use_fixedip":true,"fixed_ip":"192.168.1.28",
         "last_ip":"192.168.1.28","last_uplink_name":"Media Switch","last_seen":1794890264,"last_connection_network_name":"Main"}
        """;

    private static string LogEvent(string evt, string category, string severity, long ts, string messageRaw, string parameters) =>
        $$"""{"category":"{{category}}","event":"{{evt}}","id":"{{Guid.NewGuid()}}","message_raw":"{{messageRaw}}","parameters":{{parameters}},"severity":"{{severity}}","subcategory":"X","timestamp":{{ts}},"title_raw":"t","type":"T"}""";

    private static readonly string Roam = LogEvent("CLIENT_ROAMED", "CLIENT_DEVICES", "LOW", 1794899000000,
        "{CLIENT} roamed from {DEVICE_FROM} to {DEVICE_TO}.",
        """{"CLIENT":{"id":"cc:00:00:00:00:02","name":"phone"},"DEVICE_FROM":{"name":"Hall AP"},"DEVICE_TO":{"name":"Den AP"}}""");

    private static readonly string Threat = LogEvent("THREAT_BLOCKED", "SECURITY", "HIGH", 1794898000000,
        "A network intrusion attempt from {SRC_IP} to {DST_IP} has been detected and blocked.",
        """{"SRC_IP":{"name":"203.0.113.9"},"DST_IP":{"name":"192.168.1.50"}}""");

    private static readonly string PacketLoss = LogEvent("ISP_PACKET_LOSS", "INTERNET_AND_WAN", "MEDIUM", 1794897000000,
        "Internet connection {WAN_ID} {ISP_NAME} on port {PORT} is experiencing packet loss.",
        """{"WAN_ID":{"name":"WAN1"},"ISP_NAME":{"name":"Example ISP"},"PORT":{"name":"10"}}""");

    private static string LogPage(int total, params string[] events) =>
        $$"""{"data":[{{string.Join(",", events)}}],"page_number":0,"total_element_count":{{total}},"total_page_count":1}""";

    private const string LogCount = """
        {"categories":[{"count":540,"name":"CLIENT_DEVICES"},{"count":54,"name":"SECURITY"},{"count":1,"name":"INTERNET_AND_WAN"}],
         "events":[{"count":376,"name":"CLIENT_ROAMED"},{"count":54,"name":"THREAT_BLOCKED"},{"count":1,"name":"ISP_PACKET_LOSS"}],"type":[]}
        """;

    private const string Health = """
        {"meta":{"rc":"ok"},"data":[
          {"subsystem":"wan","status":"ok","wan_ip":"198.51.100.7","isp_name":"Example ISP","gw_name":"Gateway",
           "uptime_stats":{"WAN":{"availability":99.65,"latency_average":8,"time_period":86400,
             "monitors":[{"availability":99.65,"latency_average":8,"target":"ping.ui.com","type":"icmp"},{"availability":100,"latency_average":6,"target":"1.1.1.1","type":"icmp"}]}}},
          {"subsystem":"www","status":"ok","latency":4,"uptime":77776,"drops":2,"xput_up":457,"xput_down":1055,"speedtest_lastrun":1794837213},
          {"subsystem":"wlan","status":"warning","num_ap":5,"num_disconnected":1,"num_user":36},
          {"subsystem":"lan","status":"error","num_sw":4,"num_disconnected":2,"num_user":20}]}
        """;

    private static string Dashboard()
    {
        var history = string.Join(",", Enumerable.Range(0, 12).Select(i =>
            $$"""{"avg_latency_ms":{{(i == 7 ? 95.5 : 38.0)}},"max_latency_ms":{{(i == 7 ? 180 : 39)}},"min_latency_ms":37,"avg_packet_loss_pct":{{(i == 7 ? 14.2 : 1.0)}},"avg_rx_rate_bps":1000,"avg_tx_rate_bps":500,"timestamp":{{1794812400000 + i * 300000}}}"""));
        return $$$"""
            {"wan_activity":{"activity_by_network_group":{"WAN":{"history":[{{{history}}}]} } },
             "wan":{"wan_details":[{"isp":{"name":"Example ISP","capabilities":{"download_kilobits_per_second":2000000,"upload_kilobits_per_second":2000000}},"network_group":"WAN","network_name":"Internet 1",
               "stats":{"service_latencies":[{"latency":8,"service_name":"microsoft"},{"latency":6,"service_name":"cloudflare"}]},"status":{"interface_name":"eth9","is_active":true,"state":"up"}}]},
             "wan_history":{"wan_history_details":[{"downtime_history":[{"start":1794815000000,"end":1794815120000}],"health_history":[]}]},
             "speed_test":{"data":[{"download_mbps":1055,"upload_mbps":457,"latency_ms":8,"time":1794837213762,"wan_networkgroup":"WAN"}]}}
            """;
    }

    private const string WanStatus = """{"wan_interfaces":[{"name":"Internet 2","state":"BACKUP","wan_networkgroup":"WAN2"},{"name":"Internet 1","state":"ACTIVE","wan_networkgroup":"WAN"}]}""";

    private static void Seed(FakeUnifi f)
    {
        f.WithSite();
        f.Route("GET", $"{Classic}/stat/sysinfo", Ok(SysInfo));
        f.Route("GET", $"{Classic}/stat/device", Ok(Ap, OfflineAp, Switch, Gateway));
        f.Route("GET", $"{Classic}/stat/sta", Ok(WiredClient, WirelessClient));
        f.Route("GET", $"{Classic}/stat/health", Health);
        f.Route("GET", $"{Classic}/stat/rogueap", Ok(
            """{"bssid":"ee:00:00:00:00:01","essid":"Neighbour1","band":"ng","channel":6,"signal":-60,"ap_mac":"aa:00:00:00:00:01"}""",
            """{"bssid":"ee:00:00:00:00:02","essid":"Neighbour2","band":"ng","channel":6,"signal":-72,"ap_mac":"aa:00:00:00:00:01"}""",
            """{"bssid":"ee:00:00:00:00:03","essid":"FarAway","band":"ng","channel":6,"signal":-91,"ap_mac":"aa:00:00:00:00:01"}""",
            """{"bssid":"ee:00:00:00:00:04","essid":"Other","band":"na","channel":149,"signal":-70,"ap_mac":"aa:00:00:00:00:01"}"""));
        f.Route("GET", $"{Classic}/rest/portforward", Ok(
            """{"_id":"p1","name":"web","enabled":true,"proto":"tcp_udp","dst_port":"80","fwd":"192.168.1.120","fwd_port":"80","src":"any","pfwd_interface":"wan","log":false}""",
            """{"_id":"p2","name":"game","enabled":false,"proto":"udp","dst_port":"3074","fwd":"192.168.1.28","fwd_port":"3074","src":"203.0.113.0/24","pfwd_interface":"both"}"""));
        f.Route("GET", $"{V2}/clients/history", $"[{OfflineClient}]");
        f.Route("GET", $"{V2}/aggregated-dashboard", Dashboard());
        f.Route("GET", $"{V2}/wan/load-balancing/status", WanStatus);
        f.Route("POST", $"{V2}/system-log/all", LogPage(3, Roam, Threat, PacketLoss));
        f.Route("POST", $"{V2}/system-log/count", LogCount);
    }

    private static async Task<Harness> StartAsync()
    {
        var h = await Harness.StartAsync();
        Seed(h.Unifi);
        return h;
    }

    private static void NoSecrets(string text)
    {
        foreach (var s in new[] { MapsKey, VwireKey, "syslog-must-not-leak", "sso-must-not-leak", Harness.ApiKey })
            Assert.DoesNotContain(s, text);
    }

    private static JsonElement LastBody(Harness h, string pathEnd) =>
        JsonDocument.Parse(h.Unifi.Requests.Last(r => r.Path.Split('?')[0].EndsWith(pathEnd, StringComparison.Ordinal)).Body!).RootElement;

    // --- system_log --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task System_log_renders_each_message_with_its_parameters()
    {
        await using var h = await StartAsync();
        var (text, isError) = await h.CallAsync("system_log");
        Assert.False(isError, text);
        Assert.Contains("phone roamed from Hall AP to Den AP.", text);
        Assert.Contains("intrusion attempt from 203.0.113.9 to 192.168.1.50", text);
        Assert.Contains("THREAT_BLOCKED", text);
        Assert.Contains("HIGH", text);
        Assert.Contains("America/Los_Angeles", text); // times are console-local, and say so
        NoSecrets(text);
    }

    [Fact]
    public async Task System_log_without_filters_summarises_counts_so_the_next_call_can_filter()
    {
        await using var h = await StartAsync();
        var (text, _) = await h.CallAsync("system_log");
        Assert.Contains("CLIENT_ROAMED", text);
        Assert.Contains("376", text);
        Assert.Contains("SECURITY", text);
    }

    [Fact]
    public async Task System_log_sends_the_filters_to_the_console()
    {
        await using var h = await StartAsync();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await h.CallAsync("system_log", new()
        {
            ["hours"] = 48, ["category"] = "security, internet_and_wan", ["event"] = "THREAT_BLOCKED", ["severity"] = "high",
            ["search"] = "xbox", ["limit"] = 25, ["page"] = 2,
        });
        var body = LastBody(h, "/system-log/all");
        Assert.Equal(["SECURITY", "INTERNET_AND_WAN"], body.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["THREAT_BLOCKED"], body.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["HIGH"], body.GetProperty("severities").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("xbox", body.GetProperty("searchText").GetString());
        Assert.Equal(25, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, body.GetProperty("pageNumber").GetInt32());
        var span = body.GetProperty("timestampTo").GetInt64() - body.GetProperty("timestampFrom").GetInt64();
        Assert.Equal(48 * 3_600_000L, span);
        Assert.True(body.GetProperty("timestampTo").GetInt64() >= before);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100000, 720)]
    public async Task System_log_keeps_hours_in_range(int asked, int sent)
    {
        await using var h = await StartAsync();
        await h.CallAsync("system_log", new() { ["hours"] = asked });
        var body = LastBody(h, "/system-log/all");
        Assert.Equal(sent * 3_600_000L, body.GetProperty("timestampTo").GetInt64() - body.GetProperty("timestampFrom").GetInt64());
    }

    [Fact]
    public async Task System_log_caps_the_page_size()
    {
        await using var h = await StartAsync();
        await h.CallAsync("system_log", new() { ["limit"] = 5000 });
        Assert.Equal(200, LastBody(h, "/system-log/all").GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task System_log_says_how_to_see_more()
    {
        await using var h = await StartAsync();
        h.Unifi.Route("POST", $"{V2}/system-log/all", LogPage(677, Roam, Threat));
        var (text, _) = await h.CallAsync("system_log", new() { ["limit"] = 2 });
        Assert.Contains("677", text);
        Assert.Contains("page", text);
    }

    [Fact]
    public async Task System_log_reports_a_console_error_as_a_tool_error()
    {
        await using var h = await StartAsync();
        h.Unifi.Route("POST", $"{V2}/system-log/all", """{"code":"api.err.Invalid"}""", status: 400);
        var (text, isError) = await h.CallAsync("system_log");
        Assert.True(isError);
        Assert.Contains("400", text);
    }

    [Fact]
    public async Task Diagnostics_work_while_writes_are_off_and_send_no_other_post()
    {
        await using var h = await StartAsync();
        await h.CallAsync("system_log");
        Assert.All(h.Unifi.Requests.Where(r => r.Method != "GET"), r => Assert.Contains("/system-log/", r.Path));
    }

    // --- internet_health_faq ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Internet_health_shows_wans_monitors_history_and_events()
    {
        await using var h = await StartAsync();
        var (text, isError) = await h.CallAsync("internet_health_faq");
        Assert.False(isError, text);
        Assert.Contains("Internet 1", text);
        Assert.Contains("ACTIVE", text);
        Assert.Contains("BACKUP", text);
        Assert.Contains("Example ISP", text);
        Assert.Contains("99.65", text);   // monitor availability
        Assert.Contains("ping.ui.com", text);
        Assert.Contains("14.2", text);    // the worst 5-minute packet loss
        Assert.Contains("1055", text);    // last speed test
        Assert.Contains("1 outage", text);
        Assert.Contains("packet loss", text);  // the WAN event from the log
        var log = LastBody(h, "/system-log/all");
        Assert.Equal(["INTERNET_AND_WAN"], log.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        NoSecrets(text);
    }

    [Fact]
    public async Task Internet_health_asks_the_dashboard_for_the_window_requested()
    {
        await using var h = await StartAsync();
        await h.CallAsync("internet_health_faq", new() { ["hours"] = 6 });
        Assert.Contains(h.Unifi.Requests, r => r.Path.Contains("aggregated-dashboard?historySeconds=21600"));
    }

    [Fact]
    public async Task Internet_health_still_reports_when_one_step_fails()
    {
        await using var h = await StartAsync();
        h.Unifi.Route("GET", $"{V2}/aggregated-dashboard", "{}", status: 500);
        var (text, _) = await h.CallAsync("internet_health_faq");
        Assert.Contains("step failed", text);
        Assert.Contains("99.65", text);
    }

    // --- wifi_health_faq --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Wifi_health_lists_radios_and_flags_problems()
    {
        await using var h = await StartAsync();
        var (text, isError) = await h.CallAsync("wifi_health_faq");
        Assert.False(isError, text);
        Assert.Contains("Hall AP", text);
        Assert.Contains("22.5", text);     // retries
        Assert.Contains("71", text);       // channel utilisation
        Assert.Contains("Garage AP", text); // offline AP called out
        Assert.Contains("2 neighbour", text); // two strong networks share channel 6; the -91 one is too weak to count
        NoSecrets(text);
    }

    // --- client_diagnostics_faq -------------------------------------------------------------------------------------

    [Fact]
    public async Task Client_diagnostics_for_a_wireless_client_shows_signal_and_ap()
    {
        await using var h = await StartAsync();
        var (text, isError) = await h.CallAsync("client_diagnostics_faq", new() { ["client"] = "phone" });
        Assert.False(isError, text);
        Assert.Contains("Hall AP", text);
        Assert.Contains("-79", text);
        Assert.Contains("Home", text);
        Assert.Contains("31.5", text);
        Assert.Contains("roamed", text);    // its recent events
        Assert.Equal("cc:00:00:00:00:02", LastBody(h, "/system-log/all").GetProperty("searchText").GetString());
    }

    [Fact]
    public async Task Client_diagnostics_for_a_wired_client_shows_switch_port_and_reservation()
    {
        await using var h = await StartAsync();
        var (text, _) = await h.CallAsync("client_diagnostics_faq", new() { ["client"] = "192.168.1.50" });
        Assert.Contains("Core Switch", text);
        Assert.Contains("port 2", text);
        Assert.Contains("100", text);   // link speed
        Assert.Contains("Fixed IP", text);
        Assert.Contains("192.168.1.50", text);
    }

    [Fact]
    public async Task Client_diagnostics_finds_a_client_that_is_offline()
    {
        await using var h = await StartAsync();
        var (text, _) = await h.CallAsync("client_diagnostics_faq", new() { ["client"] = "attic" });
        Assert.Contains("offline", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("192.168.1.28", text);
        Assert.Contains("Media Switch", text);
    }

    [Fact]
    public async Task Client_diagnostics_reports_no_match()
    {
        await using var h = await StartAsync();
        var (text, _) = await h.CallAsync("client_diagnostics_faq", new() { ["client"] = "toaster" });
        Assert.Contains("No client", text);
    }

    // --- device_diagnostics_faq -------------------------------------------------------------------------------------

    [Fact]
    public async Task Device_diagnostics_summary_covers_every_device()
    {
        await using var h = await StartAsync();
        var (text, isError) = await h.CallAsync("device_diagnostics_faq");
        Assert.False(isError, text);
        foreach (var name in new[] { "Hall AP", "Garage AP", "Core Switch", "Gateway" }) Assert.Contains(name, text);
        Assert.Contains("offline", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7.4.12", text); // firmware available
        NoSecrets(text);
    }

    [Fact]
    public async Task Device_diagnostics_for_a_switch_flags_the_bad_port()
    {
        await using var h = await StartAsync();
        var (text, _) = await h.CallAsync("device_diagnostics_faq", new() { ["device"] = "core" });
        Assert.Contains("NAS", text);
        Assert.Contains("1532", text);   // rx errors
        Assert.Contains("14", text);     // link downs
        Assert.Contains("half", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("61", text);     // temperature
        Assert.Contains("21.5", text);   // PoE used
        NoSecrets(text);
    }

    [Fact]
    public async Task Device_diagnostics_for_the_gateway_shows_temperatures()
    {
        await using var h = await StartAsync();
        var (text, _) = await h.CallAsync("device_diagnostics_faq", new() { ["device"] = "192.168.1.1" });
        Assert.Contains("CPU", text);
        Assert.Contains("45", text);
        Assert.Contains("53.2", text);
    }

    // --- list_port_forwards -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Port_forwards_are_listed()
    {
        await using var h = await StartAsync();
        var (text, isError) = await h.CallAsync("list_port_forwards");
        Assert.False(isError, text);
        Assert.Contains("192.168.1.120", text);
        Assert.Contains("3074", text);
        Assert.Contains("203.0.113.0/24", text);
        Assert.Contains("disabled", text, StringComparison.OrdinalIgnoreCase);
    }

    // --- site, configuration and the client's allow-list -------------------------------------------------------------

    [Fact]
    public async Task A_site_given_by_uuid_uses_its_internal_reference()
    {
        await using var h = await StartAsync();
        await h.CallAsync("list_port_forwards", new() { ["siteId"] = Harness.SiteId });
        Assert.Contains(h.Unifi.Requests, r => r.Path == $"{Classic}/rest/portforward");
    }

    [Fact]
    public async Task Unconfigured_diagnostics_send_nothing()
    {
        await using var h = await Harness.StartAsync(key: false);
        var (text, _) = await h.CallAsync("system_log");
        Assert.Contains("not configured", text);
        Assert.Empty(h.Unifi.Requests);
    }

    // --- the audit log: who ran which tool, never with what -----------------------------------------------------------

    [Fact]
    public async Task A_diagnostic_call_writes_one_audit_line_with_the_login_and_no_arguments()
    {
        await using var h = await StartAsync();
        const string Arg = "argument-value-must-not-be-logged";
        await h.CallAsync("client_diagnostics_faq", new() { ["client"] = Arg });
        var line = Assert.Single(h.Logs.Entries, e => e.Line.StartsWith($"{Harness.Login} client_diagnostics_faq ", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, line.Level); // a read
        Assert.EndsWith("-> ok", line.Line);
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains(Arg, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_diagnostic_call_is_audited_as_an_error()
    {
        await using var h = await Harness.StartAsync(key: false);
        await h.CallAsync("system_log", new() { ["search"] = "argument-value-must-not-be-logged" });
        var line = Assert.Single(h.Logs.Lines, l => l.StartsWith($"{Harness.Login} system_log ", StringComparison.Ordinal));
        Assert.EndsWith("-> error", line);
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains("argument-value-must-not-be-logged", StringComparison.Ordinal));
    }

    // --- the host's clock reaches the throttle, the cache and the system-log window ----------------------------------

    /// <summary>The real host with a fake clock registered, as a test would; far from today so the real clock cannot pass for it.</summary>
    private static async Task<(Harness H, FakeTimeProvider Time)> StartWithClockAsync()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var h = await Harness.StartAsync(configure: b => b.Services.AddSingleton<TimeProvider>(time));
        Seed(h.Unifi);
        return (h, time);
    }

    [Fact]
    public async Task Through_the_host_the_internal_api_cache_follows_the_hosts_clock()
    {
        var (h, time) = await StartWithClockAsync();
        await using var _ = h;
        int DeviceReads() => h.Unifi.Requests.Count(r => r.Path == $"{Classic}/stat/device");

        await h.CallAsync("device_diagnostics_faq");
        await h.CallAsync("device_diagnostics_faq");
        Assert.Equal(1, DeviceReads());

        time.Advance(ConsoleReads.CacheFor + TimeSpan.FromSeconds(1));
        await h.CallAsync("device_diagnostics_faq");
        Assert.Equal(2, DeviceReads());
    }

    [Fact]
    public async Task Through_the_host_the_internal_api_throttle_refills_by_the_hosts_clock()
    {
        var (h, time) = await StartWithClockAsync();
        await using var _ = h;
        // A different page each time, so the cache does not absorb the calls and no counts are fetched.
        string? refused = null;
        for (var page = 1; page <= ConsoleReads.Burst + 1 && refused is null; page++)
        {
            var (text, isError) = await h.CallAsync("system_log", new() { ["page"] = page });
            if (isError) refused = text;
        }
        Assert.NotNull(refused);
        Assert.Contains("Slow down", refused);

        time.Advance(TimeSpan.FromSeconds(60.0 / ConsoleReads.PerMinute + 0.1));
        var (after, afterError) = await h.CallAsync("system_log", new() { ["page"] = 99 });
        Assert.False(afterError, after);
    }

    [Fact]
    public async Task System_log_window_comes_from_the_hosts_clock()
    {
        var (h, time) = await StartWithClockAsync();
        await using var _ = h;
        await h.CallAsync("system_log", new() { ["hours"] = 6 });
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var body in new[] { LastBody(h, "/system-log/all"), LastBody(h, "/system-log/count") })
        {
            Assert.Equal(now, body.GetProperty("timestampTo").GetInt64());
            Assert.Equal(now - 6 * 3_600_000L, body.GetProperty("timestampFrom").GetInt64());
        }
    }
}
