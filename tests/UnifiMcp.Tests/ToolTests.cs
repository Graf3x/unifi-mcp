using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

public class ToolTests
{
    private const int GuideAndFaqTools = 15;
    private const string AclRule = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task With_writes_off_only_read_tools_are_listed()
    {
        await using var h = await Harness.StartAsync();
        var tools = await h.Client.ListToolsAsync();
        Assert.Equal(GuideAndFaqTools, tools.Count); // the 41 reads run through call_read_operation
        Assert.Contains(tools, t => t.Name == "call_read_operation");
        Assert.DoesNotContain(tools, t => t.Name == "get_info");
        Assert.DoesNotContain(tools, t => t.Name == "create_acl_rule");
        Assert.All(tools, t =>
        {
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.Name);
            Assert.NotEqual(true, t.ProtocolTool.Annotations?.DestructiveHint);
        });
    }

    [Fact]
    public async Task With_writes_on_every_write_tool_is_destructive_and_needs_confirm()
    {
        await using var h = await Harness.StartAsync(writes: true);
        var tools = await h.Client.ListToolsAsync();
        Assert.Equal(32 + GuideAndFaqTools, tools.Count);
        var writes = tools.Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint != true).ToList();
        Assert.Equal(32, writes.Count);
        Assert.All(writes, t =>
        {
            Assert.True(t.ProtocolTool.Annotations?.DestructiveHint, t.Name);
            Assert.Contains("confirm", t.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        });
    }

    [Fact]
    public async Task A_disabled_write_tool_cannot_be_called()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite();
        await Assert.ThrowsAnyAsync<McpException>(() => h.CallAsync("delete_dns_policy", new() { ["dnsPolicyId"] = AclRule, ["confirm"] = "delete_dns_policy" }));
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task A_write_without_confirm_previews_and_sends_nothing()
    {
        await using var h = await Harness.StartAsync(writes: true);
        var (text, _) = await h.CallAsync("create_dns_policy", new() { ["body"] = new { type = "A_RECORD" }, ["confirm"] = "" });
        Assert.Contains("Nothing was sent", text);
        Assert.Contains("POST", text);
        Assert.Contains("create_dns_policy", text);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task A_write_with_the_wrong_confirm_sends_nothing()
    {
        await using var h = await Harness.StartAsync(writes: true);
        await h.CallAsync("create_dns_policy", new() { ["body"] = new { type = "A_RECORD" }, ["confirm"] = "true" });
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task A_confirmed_write_posts_the_body_to_the_resolved_site()
    {
        await using var h = await Harness.StartAsync(writes: true);
        h.Unifi.WithSite().Route("POST", $"/v1/sites/{Harness.SiteId}/dns/policies", """{"id":"new"}""", 201);
        var (text, isError) = await h.CallAsync("create_dns_policy", new() { ["body"] = new { type = "A_RECORD", domain = "x.test" }, ["confirm"] = "create_dns_policy" });
        Assert.False(isError, text);
        Assert.Contains("new", text);
        var post = Assert.Single(h.Unifi.Requests, r => r.Method == "POST");
        Assert.Contains("A_RECORD", post.Body);
        Assert.Equal(Harness.ApiKey, post.ApiKey);
    }

    [Fact]
    public async Task A_confirmed_write_is_audited_at_information_with_its_request_but_never_its_body()
    {
        await using var h = await Harness.StartAsync(writes: true);
        h.Unifi.WithSite().Route("POST", $"/v1/sites/{Harness.SiteId}/dns/policies", """{"id":"new"}""", 201);
        const string Secret = "body-value-must-not-be-logged";
        await h.CallAsync("create_dns_policy", new() { ["body"] = new { type = "A_RECORD", domain = Secret }, ["confirm"] = "create_dns_policy" });

        var audit = h.Logs.Entries.Where(e => e.Line.StartsWith($"{Harness.Login} create_dns_policy ", StringComparison.Ordinal)).ToList();
        Assert.All(audit, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, e.Level));
        // The request the tool sent (method, path, status) and the call's outcome.
        Assert.Single(audit, e => e.Line == $"{Harness.Login} create_dns_policy POST /v1/sites/{Harness.SiteId}/dns/policies -> 201");
        Assert.Single(audit, e => e.Line == $"{Harness.Login} create_dns_policy -> ok");
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_fills_the_path_and_passes_paging_and_filter()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite().Route("GET", $"/v1/sites/{Harness.SiteId}/networks", FakeUnifi.Page());
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_networks_overview_page", ["arguments"] = new Dictionary<string, object?> { ["limit"] = 5, ["offset"] = 10, ["filter"] = "name.like('guest*')" } });
        Assert.False(isError, text);
        var seen = h.Unifi.Requests.Last();
        Assert.StartsWith($"/v1/sites/{Harness.SiteId}/networks?", seen.Path);
        Assert.Contains("limit=5", seen.Path);
        Assert.Contains("offset=10", seen.Path);
        Assert.Contains("filter=name.like%28%27guest%2A%27%29", seen.Path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task Dot_segments_are_refused_as_path_values(string value)
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite();
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_acl_rule", ["arguments"] = new Dictionary<string, object?> { ["aclRuleId"] = value } });
        Assert.True(isError);
        Assert.Contains("aclRuleId", text);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Path.Contains("acl-rules"));
    }

    [Fact]
    public async Task A_slash_in_a_path_value_stays_inside_one_segment()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite();
        await h.CallAsync("call_read_operation", new() { ["operation"] = "get_acl_rule", ["arguments"] = new Dictionary<string, object?> { ["aclRuleId"] = "../../../firewall" } });
        Assert.Contains(h.Unifi.Requests, r => r.Path.EndsWith("/acl-rules/..%2F..%2F..%2Ffirewall", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_required_argument_is_named()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite();
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_acl_rule" });
        Assert.True(isError);
        Assert.Contains("aclRuleId", text);
    }

    [Fact]
    public async Task A_read_while_not_configured_says_so_and_sends_nothing()
    {
        await using var h = await Harness.StartAsync(key: false, pin: false);
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_info" });
        Assert.True(isError);
        Assert.Contains("not configured", text);
        Assert.Empty(h.Unifi.Requests);
    }

    [Fact]
    public async Task A_console_error_is_a_tool_error_without_the_key()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.Route("GET", "/v1/info", $$"""{"message":"nope {{Harness.ApiKey}}"}""", 403);
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_info" });
        Assert.True(isError);
        Assert.Contains("403", text);
        Assert.DoesNotContain(Harness.ApiKey, text);
    }

    [Fact]
    public async Task A_huge_answer_is_truncated_with_a_hint()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.Route("GET", "/v1/info", "\"" + new string('x', 200_000) + "\"");
        var (text, _) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_info" });
        // Under Claude Code's 50,000-character point where a result is moved to a file, and well under 25k tokens.
        Assert.True(text.Length <= ToolText.Limit + 200, $"{text.Length}");
        Assert.True(ToolText.Limit < 50_000);
        Assert.Contains("truncated", text);
    }

    [Fact]
    public async Task A_huge_composite_answer_is_truncated_too()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite();
        var many = string.Join(",", Enumerable.Range(0, 3000).Select(i =>
            $$"""{"id":"c{{i}}","name":"client-{{i}}-{{new string('n', 20)}}","type":"WIRED","ipAddress":"10.9.{{i / 250}}.{{i % 250}}","macAddress":"aa:bb:cc:00:{{i / 256:x2}}:{{i % 256:x2}}"}"""));
        h.Unifi.Route("GET", $"/v1/sites/{Harness.SiteId}/clients", $$"""{"offset":0,"limit":200,"count":3000,"totalCount":3000,"data":[{{many}}]}""");
        h.Unifi.Route("GET", $"/v1/sites/{Harness.SiteId}/networks", FakeUnifi.Page());
        h.Unifi.Route("GET", $"/v1/sites/{Harness.SiteId}/devices", FakeUnifi.Page());
        var (text, _) = await h.CallAsync("who_is_on_network_faq");
        Assert.True(text.Length <= ToolText.Limit + 200, $"{text.Length}");
        Assert.Contains("truncated", text);
    }
}

public class GuideToolTests
{
    [Fact]
    public async Task Getting_started_reports_not_configured()
    {
        await using var h = await Harness.StartAsync(key: false, pin: false);
        var (text, isError) = await h.CallAsync("getting_started");
        Assert.False(isError);
        Assert.Contains("not configured", text);
        Assert.Contains("API key", text);
        Assert.Contains("read-only", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Unifi.Requests);
    }

    [Fact]
    public async Task Status_reports_version_and_site_when_configured()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite().Route("GET", "/v1/info", """{"applicationVersion":"11.0.97"}""");
        var (text, isError) = await h.CallAsync("unifi_status");
        Assert.False(isError, text);
        Assert.Contains("11.0.97", text);
        Assert.Contains(Harness.SiteId, text);
        Assert.DoesNotContain(Harness.ApiKey, text);
    }

    [Fact]
    public async Task Search_finds_operations_including_disabled_writes()
    {
        await using var h = await Harness.StartAsync();
        var (text, _) = await h.CallAsync("search_operations", new() { ["query"] = "dns" });
        Assert.Contains("get_dns_policy_page", text);
        Assert.Contains("create_dns_policy", text);
        Assert.Contains("disabled", text);
    }

    [Fact]
    public async Task Describe_shows_the_body_schema_of_a_disabled_write()
    {
        await using var h = await Harness.StartAsync();
        var (text, isError) = await h.CallAsync("describe_operation", new() { ["operation"] = "create_dns_policy" });
        Assert.False(isError, text);
        Assert.Contains("A_RECORD", text);
        Assert.Contains("disabled", text);
    }

    [Fact]
    public async Task Describe_of_an_unknown_operation_is_a_clear_error()
    {
        await using var h = await Harness.StartAsync();
        var (text, isError) = await h.CallAsync("describe_operation", new() { ["operation"] = "reboot_everything" });
        Assert.True(isError);
        Assert.Contains("search_operations", text);
    }

    // --- call_read_operation: one tool for the 41 reads ---------------------------------------------------------------

    [Fact]
    public async Task Call_read_operation_lists_every_read_and_no_write_in_its_schema()
    {
        await using var h = await Harness.StartAsync(writes: true);
        var tool = (await h.Client.ListToolsAsync()).Single(t => t.Name == "call_read_operation").ProtocolTool;
        var names = tool.InputSchema.GetProperty("properties").GetProperty("operation").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(41, names.Count);
        Assert.Contains("get_info", names);
        Assert.DoesNotContain("create_dns_policy", names);
        Assert.True(tool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, tool.Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Call_read_operation_refuses_a_write_and_names_its_own_tool()
    {
        await using var h = await Harness.StartAsync(writes: true);
        h.Unifi.WithSite();
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "delete_dns_policy", ["arguments"] = new Dictionary<string, object?> { ["dnsPolicyId"] = "x" } });
        Assert.True(isError);
        Assert.Contains("delete_dns_policy", text);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");
    }

    [Fact]
    public async Task Call_read_operation_names_valid_arguments_when_given_an_unknown_one()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite();
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_acl_rule", ["arguments"] = new Dictionary<string, object?> { ["ruleId"] = "x" } });
        Assert.True(isError);
        Assert.Contains("ruleId", text);
        Assert.Contains("aclRuleId", text);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Path.Contains("acl-rules"));
    }

    [Fact]
    public async Task Call_read_operation_takes_parameters_given_beside_operation()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite();
        await h.CallAsync("call_read_operation", new() { ["operation"] = "get_acl_rule", ["aclRuleId"] = "r1" });
        Assert.Contains(h.Unifi.Requests, r => r.Path.EndsWith("/acl-rules/r1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Call_read_operation_points_an_unknown_operation_at_search()
    {
        await using var h = await Harness.StartAsync();
        var (text, isError) = await h.CallAsync("call_read_operation", new() { ["operation"] = "get_everything" });
        Assert.True(isError);
        Assert.Contains("search_operations", text);
    }

    [Fact]
    public async Task Search_shows_each_operations_parameters_and_how_to_run_it()
    {
        await using var h = await Harness.StartAsync();
        var (text, _) = await h.CallAsync("search_operations", new() { ["query"] = "acl rule" });
        Assert.Contains("aclRuleId*", text);   // required parameters are starred
        Assert.Contains("call_read_operation", text);
        Assert.StartsWith("| operation | method | category | summary | parameters | run with |\n|---|---|---|---|---|---|\n", text);
        Assert.DoesNotContain("\r", text);     // the shared table: one line ending everywhere
    }
}
