using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

public class AnswerTests
{
    private sealed record SampleView(string View, string? SiteId = null, string? Message = null, string? Detail = null);

    private static Task<T> Fail<T>(Exception e) => Task.FromException<T>(e);

    private static string Text(CallToolResult r) => ((TextContentBlock)Assert.Single(r.Content)).Text;

    private static async Task<string> TextOf(Func<Answer, Task> body) => Text(await Answer.RunAsync(body));

    [Fact]
    public async Task A_failed_step_is_reported_and_the_rest_still_runs()
    {
        var text = await TextOf(async a =>
        {
            a.Line("before");
            var devices = await a.Step("Devices", () => Fail<List<string>>(new UnifiError("The console answered 500.")));
            Assert.Null(devices);
            a.Line("after");
        });

        Assert.Contains("**Devices step failed:** The console answered 500.", text);
        Assert.True(text.IndexOf("before", StringComparison.Ordinal) < text.IndexOf("step failed", StringComparison.Ordinal));
        Assert.Contains("after", text);
    }

    [Fact]
    public async Task A_step_hands_back_what_it_read()
    {
        using var doc = JsonDocument.Parse("""{"name":"UDM"}""");
        JsonElement? read = null;
        List<string>? list = null;
        await TextOf(async a =>
        {
            read = await a.Step("Console", () => Task.FromResult(doc.RootElement));
            list = await a.Step("Devices", () => Task.FromResult(new List<string> { "UDM" }));
        });

        Assert.Equal("UDM", read?.GetProperty("name").GetString());
        Assert.Equal(["UDM"], list);
    }

    [Fact]
    public async Task A_json_step_that_fails_hands_back_nothing()
    {
        JsonElement? read = default(JsonElement);
        var text = await TextOf(async a =>
            read = await a.Step("Dashboard", () => Fail<JsonElement>(new UnifiError("no dashboard"))));

        Assert.Null(read);
        Assert.Contains("**Dashboard step failed:** no dashboard", text);
    }

    public static TheoryData<Exception> UnexpectedShapes => new()
    {
        new InvalidOperationException("The requested operation requires an element of type 'Object', but the target element has type 'Array'."),
        new KeyNotFoundException("The given key was not present in the dictionary."),
        new FormatException("One of the identified items was in an invalid format."),
    };

    [Theory]
    [MemberData(nameof(UnexpectedShapes))]
    public async Task A_console_answer_of_an_unexpected_shape_fails_only_its_step(Exception shape)
    {
        var text = await TextOf(async a =>
        {
            Assert.Null(await a.Step("Devices", () => Fail<List<string>>(shape)));
            Assert.Null(await a.Step("Dashboard", () => Fail<JsonElement>(shape)));
            a.Line("after");
        });

        Assert.Contains("**Devices step failed:** The console's answer was not what this server expects.", text);
        Assert.Contains("**Dashboard step failed:** The console's answer was not what this server expects.", text);
        Assert.DoesNotContain(shape.Message, text);
        Assert.Contains("after", text);
    }

    [Fact]
    public async Task A_failed_step_is_not_a_tool_error()
    {
        var result = await Answer.RunAsync(async a => await a.Step("Devices", () => Fail<List<string>>(new UnifiError("boom"))));

        Assert.NotEqual(true, result.IsError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failure_outside_a_step_is_a_tool_error_that_keeps_what_was_written(bool notConfigured)
    {
        var result = await Answer.RunAsync(a =>
        {
            a.Line("## Partial");
            Exception e = notConfigured ? new UnifiNotConfigured("UniFi MCP is not configured.") : new UnifiError("The console has no site 'x'.");
            throw e;
        });

        Assert.True(result.IsError);
        Assert.StartsWith("## Partial", Text(result));
        Assert.Contains(notConfigured ? "not configured" : "no site 'x'", Text(result));
    }

    [Fact]
    public async Task An_answer_without_a_view_has_no_meta()
    {
        var result = await Answer.RunAsync(a => { a.Line("text"); return Task.CompletedTask; });

        Assert.Null(result.Meta);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public async Task A_view_travels_in_meta_beside_one_text_block_and_is_not_capped()
    {
        var detail = new string('d', ToolText.Limit + 10);
        var result = await Answer.RunAsync(a =>
        {
            a.Line("Devices: 2");
            a.View = new SampleView("devices", Detail: detail);
            return Task.CompletedTask;
        });

        Assert.Equal("Devices: 2" + Environment.NewLine, Text(result));
        var view = result.Meta?[ViewResources.ViewMetaKey];
        Assert.Equal("devices", (string?)view?["view"]);
        Assert.Equal(detail, (string?)view?["detail"]);
        Assert.False(view!.AsObject().ContainsKey("siteId"), "nulls are left out");
        Assert.Null(result.StructuredContent);
        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task A_failure_outside_a_step_shows_the_fatal_view()
    {
        var result = await Answer.RunAsync(a =>
        {
            a.View = new SampleView("devices");
            throw new UnifiError("The console did not answer in time.");
        }, onFatal: message => new SampleView("message", Message: message));

        Assert.True(result.IsError);
        var view = result.Meta?[ViewResources.ViewMetaKey];
        Assert.Equal("message", (string?)view?["view"]);
        Assert.Equal("The console did not answer in time.", (string?)view?["message"]);
    }

    [Fact]
    public async Task An_unexpected_exception_is_not_an_answer()
    {
        await Assert.ThrowsAsync<NullReferenceException>(() => TextOf(a => throw new NullReferenceException()));
        await Assert.ThrowsAsync<NullReferenceException>(() => TextOf(async a =>
            await a.Step("Devices", () => Fail<List<string>>(new NullReferenceException()))));
    }

    [Fact]
    public async Task A_long_answer_is_capped_with_a_marker()
    {
        var text = await TextOf(a =>
        {
            a.Line(new string('x', ToolText.Limit + 1000));
            return Task.CompletedTask;
        });

        Assert.True(text.Length <= ToolText.Limit + 200, $"{text.Length} characters");
        Assert.Contains("[truncated:", text);
    }

    [Fact]
    public async Task Text_past_the_cap_is_not_kept_even_from_one_large_write()
    {
        var kept = new List<int>();
        var text = await TextOf(a =>
        {
            a.Write(new string('x', 3 * ToolText.Limit));
            kept.Add(a.ToString().Length);
            a.Line(new string('y', 3 * ToolText.Limit)).Line("more");
            kept.Add(a.ToString().Length);
            return Task.CompletedTask;
        });

        Assert.All(kept, n => Assert.Equal(ToolText.Limit + 1, n));
        Assert.Contains("[truncated:", text);

        // One character past the cap is enough for the marker; a text exactly at the cap has none.
        var line = await TextOf(a =>
        {
            a.Write(new string('x', ToolText.Limit - 1)).Line("abc");
            return Task.CompletedTask;
        });
        Assert.Contains("[truncated:", line);
        var exact = await TextOf(a =>
        {
            a.Write(new string('x', ToolText.Limit));
            return Task.CompletedTask;
        });
        Assert.DoesNotContain("[truncated:", exact);
    }

    [Fact]
    public async Task Write_and_line_build_the_text_in_order()
    {
        var text = await TextOf(a =>
        {
            a.Write("# Title").Line(" matching x").Line().Line("body");
            return Task.CompletedTask;
        });

        Assert.Equal($"# Title matching x{Environment.NewLine}{Environment.NewLine}body{Environment.NewLine}", text);
    }

    [Fact]
    public void A_table_has_one_line_ending_and_nothing_in_a_cell_can_break_it()
    {
        var table = Answer.Table(["Name|alias", "IP"], [["a|b", "192.168.1.5"], ["first\nsecond", "c\r\nd"]]);

        Assert.Equal("| Name\\|alias | IP |\n|---|---|\n| a\\|b | 192.168.1.5 |\n| first second | c d |\n", table);
    }
}
