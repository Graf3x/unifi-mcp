using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

/// <summary>
/// The interactive view (MCP Apps, extension io.modelcontextprotocol/ui) that device_diagnostics_faq carries: one tool
/// declares it, one static resource serves it, and the page itself loads nothing and writes no markup.
/// </summary>
public class ViewTests
{
    private const string Uri = "ui://unifi-network/device-diagnostics.html";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_device_diagnostics_declares_a_view(bool writes)
    {
        await using var h = await Harness.StartAsync(writes: writes);
        var tools = (await h.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToList();
        Assert.Equal(writes ? 47 : 15, tools.Count);
        var withView = tools.Where(t => t.Meta?["ui"] is not null).ToList();
        Assert.Equal("device_diagnostics_faq", Assert.Single(withView).Name);
        Assert.Equal(Uri, withView[0].Meta!["ui"]!["resourceUri"]!.GetValue<string>());
        Assert.Null(withView[0].Meta!["ui/resourceUri"]);
        Assert.Null(withView[0].OutputSchema);
    }

    [Fact]
    public async Task The_view_is_served_as_an_mcp_app()
    {
        await using var h = await Harness.StartAsync();
        var r = await h.Client.ReadResourceAsync(Uri);
        var html = Assert.IsType<TextResourceContents>(Assert.Single(r.Contents));
        Assert.Equal("text/html;profile=mcp-app", html.MimeType);
        Assert.Contains("ui/initialize", html.Text);
        Assert.Null(html.Meta?["ui"]);
        Assert.Empty(h.Unifi.Requests); // serving the view makes no console call
    }

    [Theory]
    [InlineData("http://")] [InlineData("https://")] [InlineData("<link")] [InlineData("src=")] [InlineData("url(")]
    [InlineData("@import")] [InlineData("innerHTML")] [InlineData("outerHTML")] [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")] [InlineData("setInterval")] [InlineData("fetch(")] [InlineData("XMLHttpRequest")]
    public void The_view_loads_nothing_and_writes_no_markup(string banned) =>
        Assert.DoesNotContain(banned, ViewResources.Html("views/device-diagnostics.html"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task The_tool_description_does_not_mention_the_view()
    {
        await using var h = await Harness.StartAsync();
        var d = (await h.Client.ListToolsAsync()).Single(t => t.Name == "device_diagnostics_faq").Description;
        Assert.Contains("what is connected", d);
        foreach (var word in new[] { "view", "interactive", "widget", "faceplate", "UI" }) Assert.DoesNotContain(word, d);
    }

    [Fact]
    public async Task Device_diagnostics_answers_with_text_and_structured_content()
    {
        await using var h = await Harness.StartAsync();
        var r = await h.CallRawAsync("device_diagnostics_faq");
        Assert.IsType<TextContentBlock>(Assert.Single(r.Content));
        Assert.NotNull(r.StructuredContent);
    }
}
