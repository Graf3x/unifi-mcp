using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

/// <summary>
/// How a write is approved: a client that can show a form asks the owner and ignores <c>confirm</c>; any other client
/// falls back to <c>confirm</c> set to the tool's own name.
/// </summary>
public class WriteApprovalTests
{
    private const string Tool = "create_dns_policy";
    private static readonly string PostPath = $"/v1/sites/{Harness.SiteId}/dns/policies";

    private static async Task<Harness> StartAsync()
    {
        var h = await Harness.StartAsync(writes: true);
        h.Unifi.WithSite().Route("POST", PostPath, """{"id":"new"}""", 201);
        return h;
    }

    private static async Task<(string Text, bool IsError)> CallAsync(McpClient mcp, Dictionary<string, object?> args)
    {
        var r = await mcp.CallToolAsync(Tool, args);
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    private static Dictionary<string, object?> Body(string domain = "x.test", string? confirm = null)
    {
        var args = new Dictionary<string, object?> { ["body"] = new { type = "A_RECORD", domain } };
        if (confirm is not null) args["confirm"] = confirm;
        return args;
    }

    [Fact]
    public async Task A_client_that_can_show_a_form_asks_the_owner_and_runs_on_accept()
    {
        await using var h = await StartAsync();
        var asked = new List<ElicitRequestParams>();
        await using var mcp = await h.ConnectAsync(elicit: p => { asked.Add(p); return "accept"; });

        var (text, isError) = await CallAsync(mcp, Body());

        Assert.False(isError, text);
        Assert.Contains("new", text);
        var form = Assert.Single(asked);
        Assert.Contains("POST", form.Message);
        Assert.Contains("/dns/policies", form.Message);
        Assert.Contains("x.test", form.Message);
        Assert.Empty(form.RequestedSchema!.Properties); // Accept or Decline, nothing to fill in
        Assert.Single(h.Unifi.Requests, r => r.Method == "POST");
        Assert.Contains($"{Harness.Login} {Tool} POST {PostPath} -> 201 (approved by form)", h.Logs.Lines);
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task The_owner_saying_no_sends_nothing_even_with_confirm_set(string action)
    {
        await using var h = await StartAsync();
        await using var mcp = await h.ConnectAsync(elicit: _ => action);

        var (text, isError) = await CallAsync(mcp, Body(confirm: Tool));

        Assert.False(isError, text);
        Assert.Contains("declined", text);
        Assert.Contains("Nothing was sent", text);
        Assert.Contains("Do not retry", text);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");
        Assert.Contains($"{Harness.Login} {Tool} declined by form", h.Logs.Lines);
    }

    [Fact]
    public async Task The_asking_round_is_audited_as_asked_not_failed()
    {
        await using var h = await StartAsync();
        await using var mcp = await h.ConnectAsync(elicit: _ => "accept");

        await CallAsync(mcp, Body());

        Assert.Contains($"{Harness.Login} {Tool} -> asked", h.Logs.Lines);
        Assert.Contains($"{Harness.Login} {Tool} -> ok", h.Logs.Lines);
        Assert.DoesNotContain($"{Harness.Login} {Tool} -> failed", h.Logs.Lines);
    }

    [Fact]
    public async Task A_long_body_is_capped_in_the_form()
    {
        await using var h = await StartAsync();
        string? message = null;
        await using var mcp = await h.ConnectAsync(elicit: p => { message = p.Message; return "decline"; });

        await CallAsync(mcp, Body(domain: new string('x', 20_000)));

        Assert.NotNull(message);
        Assert.True(message.Length < 4_000, $"{message.Length} characters");
        Assert.Contains("truncated", message);
    }

    [Fact]
    public async Task A_client_on_an_older_revision_falls_back_to_confirm()
    {
        await using var h = await StartAsync();
        var asked = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ => { asked++; return "accept"; }, protocolVersion: "2025-11-25");

        var (preview, _) = await CallAsync(mcp, Body());
        Assert.Contains("Nothing was sent", preview);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method != "GET");

        var (text, isError) = await CallAsync(mcp, Body(confirm: Tool));
        Assert.False(isError, text);
        Assert.Equal(0, asked);
        Assert.Single(h.Unifi.Requests, r => r.Method == "POST");
        Assert.Contains($"{Harness.Login} {Tool} POST {PostPath} -> 201 (approved by confirm argument)", h.Logs.Lines);
    }

    // --- the approval token -------------------------------------------------------------------------------------------

    private static readonly IDataProtector Protector = new EphemeralDataProtectionProvider().CreateProtector("test");

    private static Dictionary<string, JsonElement> Json(string body) => new() { ["body"] = JsonDocument.Parse(body).RootElement };

    [Fact]
    public void A_token_opens_for_the_same_tool_arguments_and_owner_within_ten_minutes()
    {
        var time = new FakeTimeProvider();
        var token = WriteApproval.Seal(Protector, time, Tool, Json("""{"type":"A_RECORD"}"""), Harness.Login);
        time.Advance(TimeSpan.FromMinutes(9));

        Assert.True(WriteApproval.Opens(Protector, time, token, Tool, Json("""{"type":"A_RECORD"}"""), Harness.Login));
        // The confirm argument is not part of what was approved.
        var withConfirm = Json("""{"type":"A_RECORD"}""");
        withConfirm["confirm"] = JsonDocument.Parse("\"x\"").RootElement;
        Assert.True(WriteApproval.Opens(Protector, time, token, Tool, withConfirm, Harness.Login));
    }

    [Fact]
    public void A_token_does_not_open_for_other_arguments_another_tool_another_owner_or_late()
    {
        var time = new FakeTimeProvider();
        var args = Json("""{"type":"A_RECORD"}""");
        var token = WriteApproval.Seal(Protector, time, Tool, args, Harness.Login);

        Assert.False(WriteApproval.Opens(Protector, time, token, Tool, Json("""{"type":"CNAME"}"""), Harness.Login));
        Assert.False(WriteApproval.Opens(Protector, time, token, "delete_dns_policy", args, Harness.Login));
        Assert.False(WriteApproval.Opens(Protector, time, token, Tool, args, "someone@example.test"));
        Assert.False(WriteApproval.Opens(Protector, time, "not-a-token", Tool, args, Harness.Login));
        Assert.False(WriteApproval.Opens(Protector, time, null, Tool, args, Harness.Login));
        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.False(WriteApproval.Opens(Protector, time, token, Tool, args, Harness.Login));
    }
}
