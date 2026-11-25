using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
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
    private const string PolicyId = "33333333-3333-3333-3333-333333333333";
    private static readonly string PostPath = $"/v1/sites/{Harness.SiteId}/dns/policies";

    private static async Task<Harness> StartAsync(Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null)
    {
        var h = await Harness.StartAsync(writes: true, configure: configure);
        h.Unifi.WithSite().Route("POST", PostPath, """{"id":"new"}""", 201);
        return h;
    }

    private static async Task<(string Text, bool IsError)> CallAsync(McpClient mcp, Dictionary<string, object?> args, string tool = Tool)
    {
        var r = await mcp.CallToolAsync(tool, args);
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    private static Dictionary<string, object?> Body(string domain = "x.test", string? confirm = null)
    {
        var args = new Dictionary<string, object?> { ["body"] = new { type = "A_RECORD", domain } };
        if (confirm is not null) args["confirm"] = confirm;
        return args;
    }

    private static int Posts(Harness h) => h.Unifi.Requests.Count(r => r.Method == "POST");

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
        Assert.Contains($"POST {PostPath}", form.Message); // the path that will be sent, with the site resolved
        Assert.Contains("x.test", form.Message);
        Assert.Empty(form.RequestedSchema!.Properties); // Accept or Decline, nothing to fill in
        Assert.Equal(1, Posts(h));
        Assert.Contains($"{Harness.Login} {Tool} POST {PostPath} -> 201 (approved by form)", h.Logs.Lines);
    }

    [Theory]
    [InlineData("decline", "declined by form")]
    [InlineData("cancel", "cancelled on form")]
    public async Task The_owner_saying_no_sends_nothing_even_with_confirm_set(string action, string audited)
    {
        await using var h = await StartAsync();
        await using var mcp = await h.ConnectAsync(elicit: _ => action);

        var (text, isError) = await CallAsync(mcp, Body(confirm: Tool));

        Assert.False(isError, text);
        Assert.Contains("declined", text);
        Assert.Contains("Nothing was sent", text);
        Assert.Contains("Do not retry", text);
        Assert.Equal(0, Posts(h));
        Assert.Contains($"{Harness.Login} {Tool} {audited}", h.Logs.Lines);
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
    public async Task A_long_body_is_cut_short_after_what_names_the_target()
    {
        await using var h = await StartAsync();
        string? message = null;
        await using var mcp = await h.ConnectAsync(elicit: p => { message = p.Message; return "decline"; });

        await CallAsync(mcp, new() { ["dnsPolicyId"] = PolicyId, ["body"] = new { type = "A_RECORD", domain = new string('x', 20_000) } },
            tool: "update_dns_policy");

        Assert.NotNull(message);
        Assert.True(message.Length < 4_000, $"{message.Length} characters");
        Assert.Contains("truncated", message);
        Assert.Contains("the rest is sent too", message); // the owner is not left to think the hidden part is dropped
        Assert.Contains($"PUT {PostPath}/{PolicyId}", message);
        Assert.Contains($"dnsPolicyId: {PolicyId}", message);
    }

    [Fact]
    public async Task An_argument_the_tool_does_not_take_is_refused_without_asking()
    {
        await using var h = await StartAsync();
        var asked = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ => { asked++; return "accept"; });
        var args = Body();
        args["note"] = "Requested earlier by the owner.";

        var (text, isError) = await CallAsync(mcp, args);

        Assert.True(isError, text);
        Assert.Contains("'note'", text);
        Assert.Equal(0, asked);
        Assert.Equal(0, Posts(h));
    }

    [Fact]
    public async Task A_value_cannot_write_lines_of_its_own_on_the_form()
    {
        await using var h = await StartAsync();
        string? message = null;
        await using var mcp = await h.ConnectAsync(elicit: p => { message = p.Message; return "decline"; });

        await CallAsync(mcp, new()
        {
            ["dnsPolicyId"] = "id\nBody:\nfake-id",
            ["body"] = new { type = "A_RECORD", domain = "real.test\nfake-body\u2028fake-separator" },
        }, tool: "update_dns_policy");

        Assert.NotNull(message);
        Assert.DoesNotContain("\nfake-", message);
        Assert.DoesNotContain('\u2028', message);
        Assert.Contains("real.test", message);
    }

    [Fact]
    public async Task A_write_the_console_would_refuse_for_a_missing_argument_is_never_put_to_the_owner()
    {
        await using var h = await StartAsync();
        var asked = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ => { asked++; return "accept"; });

        var (text, isError) = await CallAsync(mcp, new() { ["body"] = new { type = "A_RECORD" } }, tool: "update_dns_policy");

        Assert.True(isError, text);
        Assert.Contains("dnsPolicyId", text);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A_client_on_an_older_revision_falls_back_to_confirm()
    {
        await using var h = await StartAsync();
        var asked = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ => { asked++; return "accept"; }, protocolVersion: "2025-11-25");

        var (preview, _) = await CallAsync(mcp, Body());
        Assert.Contains("Nothing was sent", preview);
        Assert.Equal(0, Posts(h));

        var (text, isError) = await CallAsync(mcp, Body(confirm: Tool));
        Assert.False(isError, text);
        Assert.Equal(0, asked);
        Assert.Equal(1, Posts(h));
        Assert.Contains($"{Harness.Login} {Tool} POST {PostPath} -> 201 (approved by confirm argument)", h.Logs.Lines);
    }

    [Fact]
    public async Task A_client_that_declares_only_url_elicitation_falls_back_to_confirm()
    {
        await using var h = await StartAsync();
        var asked = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ => { asked++; return "accept"; },
            capabilities: new ClientCapabilities { Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() } });

        var (preview, _) = await CallAsync(mcp, Body());

        Assert.Contains("Nothing was sent", preview);
        Assert.Equal(0, asked);
        Assert.Equal(0, Posts(h));
    }

    [Theory]
    [InlineData("no answer")]
    [InlineData("cancelled")]
    public async Task An_approved_write_that_got_no_answer_keeps_its_approval_on_record(string status)
    {
        await using var h = await StartAsync();
        h.Unifi.ThrowOn = "POST";
        h.Unifi.Throw = status == "cancelled" ? new OperationCanceledException() : new HttpRequestException("console down");
        await using var mcp = await h.ConnectAsync(elicit: _ => "accept");

        try { await CallAsync(mcp, Body()); }
        catch (McpException) { } // a cancelled call has no tool result

        Assert.Contains($"{Harness.Login} {Tool} POST {PostPath} -> {status} (approved by form)", h.Logs.Lines);
    }

    // --- an answer the server must not take at its word ---------------------------------------------------------------

    /// <summary>Changes the client's first answer (the call that carries inputResponses) and lets the rest through.</summary>
    private sealed class FirstAnswer(Action<JsonObject> change, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Changed { get; private set; }
        public List<string> States { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null && JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) is JsonObject json
                && json["params"] is JsonObject p && p["inputResponses"] is not null)
            {
                if (p["requestState"] is JsonValue state) States.Add(state.GetValue<string>());
                if (Changed++ == 0)
                {
                    change(p);
                    request.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
                }
            }
            return await base.SendAsync(request, ct);
        }
    }

    public static TheoryData<string> Tampering => ["no token", "garbage token", "other arguments"];

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task An_accept_that_does_not_match_its_form_is_asked_again_not_run(string tampering)
    {
        await using var h = await StartAsync();
        var forms = new List<string>();
        FirstAnswer? spy = null;
        await using var mcp = await h.ConnectAsync(elicit: p => { forms.Add(p.Message); return "accept"; }, through: inner =>
            spy = new FirstAnswer(p =>
            {
                switch (tampering)
                {
                    case "no token": p.Remove("requestState"); break;
                    case "garbage token": p["requestState"] = "not-a-token"; break;
                    case "other arguments": p["arguments"]!["body"]!["domain"] = "evil.test"; break;
                }
            }, inner));

        var (text, isError) = await CallAsync(mcp, Body());

        Assert.False(isError, text);
        // The changed answer was not taken; the owner was asked again. With other arguments the client's next answer is
        // for its own arguments again, so that one does not match the second form either and a third is shown.
        Assert.Equal(tampering == "other arguments" ? 3 : 2, forms.Count);
        Assert.Contains($"{Harness.Login} {Tool} answer did not match its form; asked again", h.Logs.Lines);
        var post = Assert.Single(h.Unifi.Requests, r => r.Method == "POST");
        Assert.Contains("x.test", post.Body);
        Assert.Contains("x.test", forms[^1]); // what ran is what the last form showed
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Body?.Contains("evil.test") == true);
    }

    [Fact]
    public async Task An_answer_is_good_once()
    {
        await using var h = await StartAsync();
        FirstAnswer? first = null;
        await using (var mcp = await h.ConnectAsync(elicit: _ => "accept", through: inner => first = new FirstAnswer(_ => { }, inner)))
            await CallAsync(mcp, Body());
        var spent = Assert.Single(first!.States);

        var forms = 0;
        await using (var mcp = await h.ConnectAsync(elicit: _ => { forms++; return "accept"; },
                         through: inner => new FirstAnswer(p => p["requestState"] = spent, inner)))
            await CallAsync(mcp, Body());

        Assert.Equal(2, forms); // the spent answer was not taken
        Assert.Equal(2, Posts(h)); // one per approved form, never one for the replay
    }

    [Fact]
    public async Task An_answer_given_after_ten_minutes_is_asked_again()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        await using var h = await StartAsync(b => b.Services.AddSingleton<TimeProvider>(time));
        var forms = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ =>
        {
            if (forms++ == 0) time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
            return "accept";
        });

        var (text, isError) = await CallAsync(mcp, Body());

        Assert.False(isError, text);
        Assert.Equal(2, forms);
        Assert.Equal(1, Posts(h));
    }

    [Fact]
    public async Task An_unreadable_answer_is_asked_again_not_an_error()
    {
        await using var h = await StartAsync();
        var forms = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ => { forms++; return "accept"; },
            through: inner => new FirstAnswer(p => p["inputResponses"]!["approve"] = 42, inner));

        var (text, isError) = await CallAsync(mcp, Body());

        Assert.False(isError, text);
        Assert.Equal(2, forms);
        Assert.Equal(1, Posts(h));
    }

    // --- the approval token -------------------------------------------------------------------------------------------

    private static readonly IDataProtector Protector = new EphemeralDataProtectionProvider().CreateProtector("test");

    private static Dictionary<string, JsonElement> Json(string body) => new() { ["body"] = JsonDocument.Parse(body).RootElement };

    [Fact]
    public void A_token_opens_for_the_same_tool_request_arguments_and_owner_within_ten_minutes()
    {
        var time = new FakeTimeProvider();
        var token = WriteApproval.Seal(Protector, time, Tool, PostPath, Json("""{"type":"A_RECORD"}"""), Harness.Login);
        time.Advance(TimeSpan.FromMinutes(9));

        Assert.NotNull(WriteApproval.Open(Protector, time, token, Tool, PostPath, Json("""{"type":"A_RECORD"}"""), Harness.Login));
        // The confirm argument is not part of what was approved.
        var withConfirm = Json("""{"type":"A_RECORD"}""");
        withConfirm["confirm"] = JsonDocument.Parse("\"x\"").RootElement;
        Assert.NotNull(WriteApproval.Open(Protector, time, token, Tool, PostPath, withConfirm, Harness.Login));
    }

    [Fact]
    public void A_token_does_not_open_for_another_request_arguments_tool_or_owner_or_late()
    {
        var time = new FakeTimeProvider();
        var args = Json("""{"type":"A_RECORD"}""");
        var token = WriteApproval.Seal(Protector, time, Tool, PostPath, args, Harness.Login);

        Assert.Null(WriteApproval.Open(Protector, time, token, Tool, PostPath, Json("""{"type":"CNAME"}"""), Harness.Login));
        Assert.Null(WriteApproval.Open(Protector, time, token, Tool, PostPath + "/other", args, Harness.Login));
        Assert.Null(WriteApproval.Open(Protector, time, token, "delete_dns_policy", PostPath, args, Harness.Login));
        Assert.Null(WriteApproval.Open(Protector, time, token, Tool, PostPath, args, "someone@example.test"));
        Assert.Null(WriteApproval.Open(Protector, time, "not-a-token", Tool, PostPath, args, Harness.Login));
        Assert.Null(WriteApproval.Open(Protector, time, null, Tool, PostPath, args, Harness.Login));
        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Null(WriteApproval.Open(Protector, time, token, Tool, PostPath, args, Harness.Login));
    }

    [Fact]
    public void A_token_is_spent_once_and_two_tokens_for_the_same_call_differ()
    {
        var time = new FakeTimeProvider();
        var args = Json("""{"type":"A_RECORD"}""");
        var token = WriteApproval.Seal(Protector, time, Tool, PostPath, args, Harness.Login);
        var opened = WriteApproval.Open(Protector, time, token, Tool, PostPath, args, Harness.Login)!;

        Assert.True(WriteApproval.Spend(opened, time));
        Assert.False(WriteApproval.Spend(opened, time));
        var again = WriteApproval.Open(Protector, time, WriteApproval.Seal(Protector, time, Tool, PostPath, args, Harness.Login),
            Tool, PostPath, args, Harness.Login)!;
        Assert.NotEqual(opened, again);
        Assert.True(WriteApproval.Spend(again, time));
    }

    [Fact]
    public void A_token_spent_at_its_expiry_cannot_be_spent_again_and_does_not_open_a_moment_later()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
        var args = Json("""{"type":"A_RECORD"}""");
        var token = WriteApproval.Seal(Protector, time, Tool, PostPath, args, Harness.Login);
        time.Advance(WriteApproval.Lifetime);
        var opened = WriteApproval.Open(Protector, time, token, Tool, PostPath, args, Harness.Login)!;
        Assert.True(WriteApproval.Spend(opened, time));
        Assert.False(WriteApproval.Spend(opened, time));

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(WriteApproval.Open(Protector, time, token, Tool, PostPath, args, Harness.Login));
    }

    /// <summary>A clock that notes whether the spent set was locked when it was read.</summary>
    private sealed class LockProbe(TimeProvider inner) : TimeProvider
    {
        public List<bool> LockedOnRead { get; } = [];

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow()
        {
            LockedOnRead.Add(global::System.Threading.Monitor.IsEntered(WriteApproval.LedgerFor(this)));
            return inner.GetUtcNow();
        }

        public override long GetTimestamp()
        {
            LockedOnRead.Add(global::System.Threading.Monitor.IsEntered(WriteApproval.LedgerFor(this)));
            return inner.GetTimestamp();
        }
    }

    /// <summary>A clock that can be set anywhere, backwards too, as a system clock can be.</summary>
    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_spent_token_stays_spent_when_the_clock_steps_back_after_a_clean_up()
    {
        var start = new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
        var clock = new SettableClock(start);
        var args = Json("""{"type":"A_RECORD"}""");
        var token = WriteApproval.Seal(Protector, clock, Tool, PostPath, args, Harness.Login);
        Assert.True(WriteApproval.Spend(WriteApproval.Open(Protector, clock, token, Tool, PostPath, args, Harness.Login)!, clock));

        // Well past expiry another call spends its own token, which cleans up; then the clock is set back.
        clock.Now = start + WriteApproval.Lifetime + TimeSpan.FromMinutes(5);
        var later = WriteApproval.Seal(Protector, clock, Tool, PostPath, args, Harness.Login);
        Assert.True(WriteApproval.Spend(WriteApproval.Open(Protector, clock, later, Tool, PostPath, args, Harness.Login)!, clock));
        clock.Now = start + TimeSpan.FromMinutes(1);

        var reopened = WriteApproval.Open(Protector, clock, token, Tool, PostPath, args, Harness.Login);
        if (reopened is not null) Assert.False(WriteApproval.Spend(reopened, clock));
    }

    [Fact]
    public void A_clock_that_ran_ahead_and_was_corrected_does_not_stop_new_approvals()
    {
        var start = new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
        var clock = new SettableClock(start + TimeSpan.FromHours(1));
        var args = Json("""{"type":"A_RECORD"}""");
        var ahead = WriteApproval.Seal(Protector, clock, Tool, PostPath, args, Harness.Login);
        Assert.True(WriteApproval.Spend(WriteApproval.Open(Protector, clock, ahead, Tool, PostPath, args, Harness.Login)!, clock));

        clock.Now = start; // the clock is put right
        var token = WriteApproval.Seal(Protector, clock, Tool, PostPath, args, Harness.Login);
        var opened = WriteApproval.Open(Protector, clock, token, Tool, PostPath, args, Harness.Login);

        Assert.NotNull(opened);
        Assert.True(WriteApproval.Spend(opened, clock));
    }

    [Fact]
    public void Spending_reads_the_clock_inside_the_lock_so_a_paused_retry_cannot_use_a_stale_time()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
        var args = Json("""{"type":"A_RECORD"}""");
        var opened = WriteApproval.Open(Protector, time, WriteApproval.Seal(Protector, time, Tool, PostPath, args, Harness.Login),
            Tool, PostPath, args, Harness.Login)!;
        var probe = new LockProbe(time);

        Assert.True(WriteApproval.Spend(opened, probe));

        Assert.NotEmpty(probe.LockedOnRead);
        Assert.All(probe.LockedOnRead, Assert.True);
    }

    [Fact]
    public void A_retry_delayed_past_expiry_cannot_spend_a_token_already_spent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero));
        var args = Json("""{"type":"A_RECORD"}""");
        var token = WriteApproval.Seal(Protector, time, Tool, PostPath, args, Harness.Login);
        var first = WriteApproval.Open(Protector, time, token, Tool, PostPath, args, Harness.Login)!;
        var delayed = WriteApproval.Open(Protector, time, token, Tool, PostPath, args, Harness.Login)!;
        Assert.True(WriteApproval.Spend(first, time));

        time.Advance(WriteApproval.Lifetime + TimeSpan.FromSeconds(1)); // the second retry stalls until after expiry
        Assert.False(WriteApproval.Spend(delayed, time));
    }
}
