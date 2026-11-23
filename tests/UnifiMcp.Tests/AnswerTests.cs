using System.Text.Json;

namespace UnifiMcp.Tests;

public class AnswerTests
{
    private static Task<T> Fail<T>(Exception e) => Task.FromException<T>(e);

    [Fact]
    public async Task A_failed_step_is_reported_and_the_rest_still_runs()
    {
        var text = await Answer.RunAsync(async a =>
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
        await Answer.RunAsync(async a =>
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
        var text = await Answer.RunAsync(async a =>
            read = await a.Step("Dashboard", () => Fail<JsonElement>(new UnifiError("no dashboard"))));

        Assert.Null(read);
        Assert.Contains("**Dashboard step failed:** no dashboard", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failure_outside_a_step_ends_the_answer_and_keeps_what_was_written(bool notConfigured)
    {
        var text = await Answer.RunAsync(a =>
        {
            a.Line("## Partial");
            Exception e = notConfigured ? new UnifiNotConfigured("UniFi MCP is not configured.") : new UnifiError("The console has no site 'x'.");
            throw e;
        });

        Assert.StartsWith("## Partial", text);
        Assert.Contains(notConfigured ? "not configured" : "no site 'x'", text);
    }

    [Fact]
    public async Task An_unexpected_exception_is_not_an_answer()
    {
        await Assert.ThrowsAsync<NullReferenceException>(() => Answer.RunAsync(a => throw new NullReferenceException()));
        await Assert.ThrowsAsync<NullReferenceException>(() => Answer.RunAsync(async a =>
            await a.Step("Devices", () => Fail<List<string>>(new NullReferenceException()))));
    }

    [Fact]
    public async Task A_long_answer_is_capped_with_a_marker()
    {
        var text = await Answer.RunAsync(a =>
        {
            a.Line(new string('x', ToolText.Limit + 1000));
            return Task.CompletedTask;
        });

        Assert.True(text.Length <= ToolText.Limit + 200, $"{text.Length} characters");
        Assert.Contains("[truncated:", text);
    }

    [Fact]
    public async Task Write_and_line_build_the_text_in_order()
    {
        var text = await Answer.RunAsync(a =>
        {
            a.Write("# Title").Line(" matching x").Line().Line("body");
            return Task.CompletedTask;
        });

        Assert.Equal($"# Title matching x{Environment.NewLine}{Environment.NewLine}body{Environment.NewLine}", text);
    }

    [Fact]
    public void A_table_escapes_pipes_in_cells()
    {
        var table = Answer.Table(["Name", "IP"], [["a|b", "192.168.1.5"]]);

        Assert.StartsWith("| Name | IP |\n|---|---|\n", table);
        Assert.Contains(@"| a\|b | 192.168.1.5 |", table);
    }
}
