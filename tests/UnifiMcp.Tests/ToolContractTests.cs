using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnifiMcp.Tests;

/// <summary>
/// Every listed tool against the rules Claude's clients publish: the Connectors Directory review criteria, the Claude API's
/// tool definition rules and Claude Code's MCP limits. A tool that breaks one is dropped, truncated or rejected, so the
/// rules are checked here rather than remembered.
/// </summary>
public class ToolContractTests
{
    private const string ServerName = "unifi-network";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_tool_meets_the_client_rules(bool writes)
    {
        await using var h = await Harness.StartAsync(writes: writes);
        var tools = await h.Client.ListToolsAsync();
        var problems = new List<string>();
        foreach (var t in tools.Select(t => t.ProtocolTool))
        {
            // Claude API: ^[a-zA-Z0-9_-]{1,128}$; the directory asks for 64 or fewer; Claude Code drops mcp__<server>__<tool> over 128.
            if (!Regex.IsMatch(t.Name, "^[a-z0-9_]{1,64}$")) problems.Add($"{t.Name}: name must be snake_case, 64 characters or fewer");
            if ($"mcp__{ServerName}__{t.Name}".Length > 128) problems.Add($"{t.Name}: qualified name over 128");
            // Directory: a title and the hint that applies.
            if (string.IsNullOrWhiteSpace(t.Title) && string.IsNullOrWhiteSpace(t.Annotations?.Title)) problems.Add($"{t.Name}: no title");
            if (t.Annotations?.ReadOnlyHint != true && t.Annotations?.DestructiveHint != true) problems.Add($"{t.Name}: neither readOnlyHint nor destructiveHint");
            if (t.Annotations?.ReadOnlyHint == true && t.Annotations?.DestructiveHint == true) problems.Add($"{t.Name}: both read-only and destructive");
            // Claude Code: descriptions are cut at 4,096 characters when loaded upfront; tool search matches within that.
            if (string.IsNullOrWhiteSpace(t.Description)) problems.Add($"{t.Name}: no description");
            else if (t.Description.Length > 4096) problems.Add($"{t.Name}: description {t.Description.Length} characters");
            else if (!Regex.IsMatch(t.Description, @"^\[[A-Za-z][^\]]*\] ")) problems.Add($"{t.Name}: description does not start with its [Category]");
            // Input schema: an object at the root, no root combinator, property names 1-64 of [A-Za-z0-9_.-].
            var schema = t.InputSchema;
            if (!schema.TryGetProperty("type", out var type) || type.GetString() != "object") problems.Add($"{t.Name}: schema root is not an object");
            foreach (var combinator in new[] { "anyOf", "oneOf", "allOf" })
                if (schema.TryGetProperty(combinator, out _)) problems.Add($"{t.Name}: root {combinator}");
            if (schema.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                {
                    if (!Regex.IsMatch(p.Name, "^[A-Za-z0-9_.-]{1,64}$")) problems.Add($"{t.Name}: property '{p.Name}'");
                    if (!p.Value.TryGetProperty("description", out var d) || string.IsNullOrWhiteSpace(d.GetString())) problems.Add($"{t.Name}: property '{p.Name}' has no description");
                }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Tool_names_are_unique_and_listed_in_a_stable_order()
    {
        await using var h = await Harness.StartAsync(writes: true);
        var first = (await h.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        var second = (await h.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.Equal(first, second);
    }

    [Fact]
    public void Server_instructions_fit_the_4096_character_limit() =>
        Assert.InRange(McpHost.Instructions.Length, 1, 4096);

    [Fact]
    public async Task Every_composite_and_diagnostic_tool_takes_siteId_like_the_generated_tools()
    {
        await using var h = await Harness.StartAsync();
        var tools = await h.Client.ListToolsAsync();
        foreach (var t in tools.Where(t => t.ProtocolTool.InputSchema.TryGetProperty("properties", out var p) && (p.TryGetProperty("site", out _) || p.TryGetProperty("siteId", out _))))
            Assert.True(t.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("siteId", out _), $"{t.Name} names its site parameter differently");
    }
}
