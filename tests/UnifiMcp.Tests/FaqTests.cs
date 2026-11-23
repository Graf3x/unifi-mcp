namespace UnifiMcp.Tests;

public class FaqTests
{
    private const string Gateway = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Switch = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Lan = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Iot = "bbbbbbbb-0000-0000-0000-000000000002";
    private static string Site => $"/v1/sites/{Harness.SiteId}";

    private static void Seed(FakeUnifi f)
    {
        f.WithSite();
        f.Route("GET", $"{Site}/networks", FakeUnifi.Page(
            $$"""{"id":"{{Lan}}","name":"Main","vlanId":1,"enabled":true,"default":true,"management":"GATEWAY","metadata":{"origin":"USER_DEFINED"} }""",
            $$"""{"id":"{{Iot}}","name":"IoT","vlanId":20,"enabled":true,"default":false,"management":"GATEWAY","metadata":{"origin":"USER_DEFINED"} }"""));
        f.Route("GET", $"{Site}/networks/{Lan}", $$"""{"id":"{{Lan}}","name":"Main","vlanId":1,"ipv4Configuration":{"hostIpAddress":"192.168.1.1","prefixLength":24,"dhcpConfiguration":{"mode":"SERVER","ipAddressRange":{"start":"192.168.1.100","stop":"192.168.1.200"},"leaseTimeSeconds":86400} } }""");
        f.Route("GET", $"{Site}/networks/{Iot}", $$"""{"id":"{{Iot}}","name":"IoT","vlanId":20,"ipv4Configuration":{"hostIpAddress":"10.20.0.1","prefixLength":24} }""");
        f.Route("GET", $"{Site}/clients", FakeUnifi.Page(
            $$"""{"id":"c1","name":"nas","type":"WIRED","ipAddress":"192.168.1.50","macAddress":"aa:bb:cc:00:00:01","uplinkDeviceId":"{{Switch}}","access":{"type":"DEFAULT"} }""",
            $$"""{"id":"c2","name":"bulb","type":"WIRELESS","ipAddress":"10.20.0.9","macAddress":"aa:bb:cc:00:00:02","uplinkDeviceId":"{{Switch}}","access":{"type":"DEFAULT"} }""",
            """{"id":"c3","name":"laptop","type":"VPN","ipAddress":"172.16.0.5","access":{"type":"DEFAULT"} }"""));
        f.Route("GET", $"{Site}/devices", FakeUnifi.Page(
            $$"""{"id":"{{Gateway}}","name":"UDM","model":"UDM-SE","state":"ONLINE","firmwareUpdatable":false,"ipAddress":"192.168.1.1","macAddress":"00:00:00:00:00:01","features":[],"interfaces":[],"supported":true}""",
            $$"""{"id":"{{Switch}}","name":"Core Switch","model":"USW-24","state":"OFFLINE","firmwareUpdatable":true,"ipAddress":"192.168.1.2","macAddress":"00:00:00:00:00:02","features":[],"interfaces":[],"supported":true}"""));
        f.Route("GET", $"{Site}/devices/{Switch}", $$"""{"id":"{{Switch}}","name":"Core Switch","model":"USW-24","state":"OFFLINE","uplink":{"deviceId":"{{Gateway}}"},"interfaces":{"ports":[{"idx":1,"state":"UP","connector":"RJ45","maxSpeedMbps":1000,"speedMbps":1000}]} }""");
        f.Route("GET", $"{Site}/devices/{Gateway}", $$"""{"id":"{{Gateway}}","name":"UDM","model":"UDM-SE","state":"ONLINE","interfaces":{"ports":[]} }""");
    }

    [Fact]
    public async Task A_faq_call_writes_one_audit_line_with_the_login_and_no_arguments()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        const string Arg = "argument-value-must-not-be-logged";
        await h.CallAsync("client_path_faq", new() { ["client"] = Arg });
        var line = Assert.Single(h.Logs.Entries, e => e.Line.StartsWith($"{Harness.Login} client_path_faq ", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, line.Level); // a read
        Assert.EndsWith("-> ok", line.Line);
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains(Arg, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Who_is_on_a_network_groups_clients_by_subnet()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        var (text, isError) = await h.CallAsync("who_is_on_network_faq", new() { ["network"] = "IoT" });
        Assert.False(isError, text);
        Assert.Contains("bulb", text);
        Assert.DoesNotContain("| nas", text);
        Assert.Contains("Core Switch", text);
    }

    [Fact]
    public async Task Who_is_on_the_network_without_a_filter_lists_every_network()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        var (text, _) = await h.CallAsync("who_is_on_network_faq");
        Assert.Contains("nas", text);
        Assert.Contains("bulb", text);
        Assert.Contains("laptop", text); // outside every subnet: shown, not dropped
        Assert.Contains("no matching network", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Client_path_walks_uplinks_to_the_gateway_and_is_honest_about_the_port()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        var (text, isError) = await h.CallAsync("client_path_faq", new() { ["client"] = "192.168.1.50" });
        Assert.False(isError, text);
        Assert.Contains("nas", text);
        var sw = text.IndexOf("Core Switch", StringComparison.Ordinal);
        var gw = text.IndexOf("UDM", sw, StringComparison.Ordinal);
        Assert.True(sw >= 0 && gw > sw, text);
        Assert.Contains("did not report the switch port", text); // no internal API answer here
    }

    [Fact]
    public async Task Client_path_reports_no_match()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        var (text, _) = await h.CallAsync("client_path_faq", new() { ["client"] = "toaster" });
        Assert.Contains("No connected client", text);
    }

    [Fact]
    public async Task Dhcp_reservation_outside_the_pool_with_expected_ip_matching()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        var (text, isError) = await h.CallAsync("dhcp_reservation_faq", new() { ["client"] = "aa:bb:cc:00:00:01", ["expected_ip"] = "192.168.1.50" });
        Assert.False(isError, text);
        Assert.Contains("matches", text);
        Assert.Contains("outside", text); // 192.168.1.50 is below the 100-200 pool
        Assert.Contains("did not report this client's reservation", text);
    }

    [Fact]
    public async Task Dhcp_reservation_flags_a_mismatch()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        var (text, _) = await h.CallAsync("dhcp_reservation_faq", new() { ["client"] = "nas", ["expected_ip"] = "192.168.1.60" });
        Assert.Contains("NOT honoured", text);
    }

    private const string NasStation = """{"meta":{"rc":"ok"},"data":[{"mac":"aa:bb:cc:00:00:01","ip":"192.168.1.50","is_wired":true,"last_uplink_name":"Core Switch","sw_port":7,"wired_rate_mbps":1000,"use_fixedip":true,"fixed_ip":"192.168.1.60"}]}""";

    [Fact]
    public async Task Client_path_names_the_switch_port_when_the_console_reports_it()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        h.Unifi.Route("GET", "/proxy/network/api/s/default/stat/sta", NasStation);
        var (text, _) = await h.CallAsync("client_path_faq", new() { ["client"] = "nas" });
        Assert.Contains("Core Switch port 7", text);
        Assert.DoesNotContain("did not report", text);
    }

    [Fact]
    public async Task Client_path_says_why_the_console_did_not_report_the_port()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        h.Unifi.Route("GET", "/proxy/network/api/s/default/stat/sta", "{}", 500);
        var (text, isError) = await h.CallAsync("client_path_faq", new() { ["client"] = "nas" });
        Assert.False(isError, text);
        Assert.Contains("**Console client record step failed:**", text);
        Assert.Contains("did not report the switch port", text);
    }

    [Fact]
    public async Task A_client_the_console_has_no_record_of_is_not_a_failed_step()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        h.Unifi.Route("GET", "/proxy/network/api/s/default/stat/sta", """{"meta":{"rc":"ok"},"data":[]}""");
        var (text, _) = await h.CallAsync("dhcp_reservation_faq", new() { ["client"] = "nas" });
        Assert.DoesNotContain("step failed", text);
        Assert.Contains("did not report this client's reservation", text);
    }

    [Fact]
    public async Task Dhcp_reservation_reads_the_fixed_ip_when_the_console_reports_it()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        h.Unifi.Route("GET", "/proxy/network/api/s/default/stat/sta", NasStation);
        var (text, _) = await h.CallAsync("dhcp_reservation_faq", new() { ["client"] = "nas" });
        Assert.Contains("Fixed IP reservation: 192.168.1.60", text);
        Assert.Contains("NOT honoured", text); // it holds .50 but is reserved .60, even with no expected_ip given
        Assert.DoesNotContain("did not report", text);
    }

    [Fact]
    public async Task Network_health_reports_offline_and_upgradable_devices()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        h.Unifi.Route("GET", "/v1/info", """{"applicationVersion":"11.0.97"}""");
        var (text, _) = await h.CallAsync("network_health_faq");
        Assert.Contains("OFFLINE", text);
        Assert.Contains("Core Switch", text);
        Assert.Contains("11.0.97", text);
    }

    [Fact]
    public async Task A_failed_step_is_reported_and_the_rest_still_runs()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        h.Unifi.Route("GET", $"{Site}/devices", """{"message":"boom"}""", 500);
        var (text, isError) = await h.CallAsync("who_is_on_network_faq", new() { ["network"] = "Main" });
        Assert.False(isError, text);
        Assert.Contains("failed", text);
        Assert.Contains("nas", text);
    }

    [Fact]
    public async Task Faq_while_not_configured_says_so_as_a_tool_error()
    {
        await using var h = await Harness.StartAsync(key: false);
        var (text, isError) = await h.CallAsync("network_health_faq");
        Assert.True(isError);
        Assert.Contains("not configured", text);
        Assert.Empty(h.Unifi.Requests);
    }

    [Fact]
    public async Task A_faq_that_cannot_start_is_a_tool_error_and_audited_as_one()
    {
        await using var h = await Harness.StartAsync();
        Seed(h.Unifi);
        var (text, isError) = await h.CallAsync("network_health_faq", new() { ["siteId"] = "nowhere" });
        Assert.True(isError);
        Assert.Contains("The console has no site 'nowhere'", text);
        Assert.EndsWith("-> error", Assert.Single(h.Logs.Lines, l => l.StartsWith($"{Harness.Login} network_health_faq ", StringComparison.Ordinal)));
    }
}
