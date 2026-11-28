using ModelContextProtocol;
using Microsoft.AspNetCore.TestHost;

namespace UnifiMcp.Tests;

/// <summary>UNIFI_WRITE_TOOLS: the owner's list of the only write tools to register.</summary>
public class WriteToolsTests
{
    private const int GuideAndFaqTools = 15;
    private static readonly int AllWrites = OpenApiCatalog.Load().Operations.Count(o => o.IsWrite);
    private const string Policy = "22222222-2222-2222-2222-222222222222";

    private static Settings Parse(string? value) => Settings.FromEnvironment(k => k == "UNIFI_WRITE_TOOLS" ? value : null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" , \n")]
    public void Unset_or_blank_means_every_write_tool(string? value) => Assert.Null(Parse(value).WriteTools);

    [Fact]
    public void Names_are_split_on_commas_and_white_space_and_kept_as_written() =>
        Assert.Equal(new[] { "create_dns_policy", "Delete_Dns_Policy", "update_network", "create_dns_policy" },
            Parse(" create_dns_policy,Delete_Dns_Policy\n update_network\tcreate_dns_policy ").WriteTools);

    [Fact]
    public void Settings_print_the_list_because_names_are_not_secrets()
    {
        Assert.Contains("WriteTools = create_dns_policy,update_network", Parse("create_dns_policy update_network").ToString());
        Assert.Contains("WriteTools = (all)", Parse(null).ToString());
    }

    [Fact]
    public async Task With_a_list_only_those_write_tools_are_listed()
    {
        await using var h = await Harness.StartAsync(writes: true, writeTools: ["create_dns_policy", "delete_dns_policy", "create_dns_policy"]);
        var tools = await h.Client.ListToolsAsync();
        var writes = tools.Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint != true).Select(t => t.Name).Order().ToList();
        Assert.Equal(new[] { "create_dns_policy", "delete_dns_policy" }, writes);
        Assert.Equal(GuideAndFaqTools + 2, tools.Count);
    }

    [Fact]
    public async Task A_blank_list_lists_every_write_tool()
    {
        await using var h = await Harness.StartAsync(writes: true, writeTools: Parse("  ").WriteTools);
        Assert.Equal(GuideAndFaqTools + AllWrites, (await h.Client.ListToolsAsync()).Count);
    }

    [Theory]
    [InlineData("create_dns_polcy")]
    [InlineData("Create_DNS_Policy")] // exact and case-sensitive: a near miss is not guessed at
    [InlineData("create_*")]
    public void An_unknown_name_stops_startup_and_names_the_entry(string bad)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var settings = Harness.NewSettings(dir, writes: true, writeTools: ["delete_dns_policy", bad]);
        var e = Assert.Throws<ArgumentException>(() => McpHost.Build(settings, new FakeUnifi(), b => b.WebHost.UseTestServer()));
        Assert.Contains("UNIFI_WRITE_TOOLS", e.Message);
        Assert.Contains($"'{bad}'", e.Message);
        // The owner is shown the names that are allowed.
        Assert.Contains("create_dns_policy", e.Message);
        Assert.Contains("update_network", e.Message);
    }

    [Fact]
    public void A_read_operation_is_not_a_write_tool()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var settings = Harness.NewSettings(dir, writes: true, writeTools: ["get_info"]);
        var e = Assert.Throws<ArgumentException>(() => McpHost.Build(settings, new FakeUnifi(), b => b.WebHost.UseTestServer()));
        Assert.Contains("'get_info' is a read operation", e.Message);
    }

    [Fact]
    public void With_writes_off_a_typo_still_stops_startup()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var settings = Harness.NewSettings(dir, writes: false, writeTools: ["create_dns_polcy"]);
        var e = Assert.Throws<ArgumentException>(() => McpHost.Build(settings, new FakeUnifi(), b => b.WebHost.UseTestServer()));
        Assert.Contains("'create_dns_polcy'", e.Message);
    }

    [Fact]
    public async Task With_writes_off_a_list_registers_no_writes_and_says_it_has_no_effect()
    {
        await using var h = await Harness.StartAsync(writes: false, writeTools: ["create_dns_policy"]);
        var tools = await h.Client.ListToolsAsync();
        Assert.Equal(GuideAndFaqTools, tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "create_dns_policy");
        Assert.Single(h.Logs.Lines, l => l.Contains("UNIFI_WRITE_TOOLS") && l.Contains("no effect"));
    }

    [Fact]
    public async Task With_writes_on_the_no_effect_line_is_not_logged()
    {
        await using var h = await Harness.StartAsync(writes: true, writeTools: ["create_dns_policy"]);
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains("no effect"));
    }

    [Fact]
    public async Task A_write_tool_left_off_the_list_cannot_be_called()
    {
        await using var h = await Harness.StartAsync(writes: true, confirmFallback: true, writeTools: ["create_dns_policy"]);
        h.Unifi.WithSite();
        await Assert.ThrowsAnyAsync<McpException>(() => h.CallAsync("delete_dns_policy", new() { ["dnsPolicyId"] = Policy, ["confirm"] = "delete_dns_policy" }));
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task Call_read_operation_says_a_write_left_off_the_list_is_disabled_rather_than_pointing_at_its_tool()
    {
        await using var h = await Harness.StartAsync(writes: true, writeTools: ["create_dns_policy"]);
        h.Unifi.WithSite();
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "delete_dns_policy" });
        Assert.True(isError);
        Assert.Contains("UNIFI_WRITE_TOOLS", text);
        Assert.DoesNotContain("listed only when writes are on", text);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task Call_read_operation_points_a_listed_write_at_its_own_tool()
    {
        await using var h = await Harness.StartAsync(writes: true, writeTools: ["create_dns_policy"]);
        h.Unifi.WithSite();
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "create_dns_policy" });
        Assert.True(isError);
        Assert.Contains("Use its own tool, create_dns_policy", text);
    }

    [Fact]
    public async Task Getting_started_says_how_many_write_tools_are_on_and_that_the_owner_limited_them()
    {
        await using var h = await Harness.StartAsync(writes: true, writeTools: ["create_dns_policy", "delete_dns_policy"]);
        var (text, _) = await h.CallAsync("getting_started");
        Assert.Contains($"2 of {AllWrites} write tools", text);
        Assert.Contains("UNIFI_WRITE_TOOLS", text);
    }

    [Fact]
    public async Task Getting_started_without_a_list_lists_every_write_tool()
    {
        await using var h = await Harness.StartAsync(writes: true);
        var (text, _) = await h.CallAsync("getting_started");
        Assert.Contains($"{AllWrites} write tools are listed", text);
        Assert.DoesNotContain("UNIFI_WRITE_TOOLS", text);
    }

    [Fact]
    public async Task Describe_says_a_write_left_off_the_list_is_disabled()
    {
        await using var h = await Harness.StartAsync(writes: true, writeTools: ["create_dns_policy"]);
        var (listed, _) = await h.CallAsync("describe_operation", new() { ["operation"] = "create_dns_policy" });
        Assert.Contains("its own tool create_dns_policy", listed);
        var (left, _) = await h.CallAsync("describe_operation", new() { ["operation"] = "delete_dns_policy" });
        Assert.Contains("disabled (not in UNIFI_WRITE_TOOLS)", left);
    }

    [Fact]
    public void The_startup_summary_says_the_writes_are_limited()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var text = Harness.Summary(Harness.NewSettings(dir, writes: true, writeTools: ["create_dns_policy", "create_dns_policy"]), 16);
        Assert.Contains("writes ON (1 tool, UNIFI_WRITE_TOOLS)", text);
    }
}
