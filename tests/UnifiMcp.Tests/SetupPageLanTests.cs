namespace UnifiMcp.Tests;

/// <summary>The setup page with the LAN listener: guide 4 when there is no public URL, a fifth port next to the connector otherwise.</summary>
public sealed partial class SetupPageTests
{
    private const string LanToken = "lan-token-0123456789abcdef0123456789ab";

    private static Settings Lan(Settings s, string listen = "http://0.0.0.0:7420") => s with { LanListen = listen, LanAccessToken = LanToken };

    [Fact]
    public void A_lan_listener_without_a_public_url_is_guide_4()
    {
        Assert.Equal(4, SetupGuide.GuideOf(Lan(new Settings())));
        Assert.Equal(4, SetupGuide.GuideOf(Lan(new Settings { Listen = "http://127.0.0.1:5199" })));
        Assert.Equal(2, SetupGuide.GuideOf(Lan(new Settings { PublicBaseUrl = new Uri("https://unifi.example.com"), SignIn = SignInMode.Code })));
        Assert.Equal(3, SetupGuide.GuideOf(Lan(new Settings { PublicBaseUrl = new Uri("https://unifi.example.com") })));
    }

    [Fact]
    public async Task Guide_4_skips_the_internet_checks_and_offers_the_lan_command()
    {
        await using var s = await StartAsync(configured: false, tweak: x => Lan(x));
        await s.UnlockAsync();
        var page = await s.Http.GetStringAsync("/setup");
        Assert.Contains("data-guide=\"4\"", page);
        Assert.Contains("data-check=\"lan\"", page);
        Assert.DoesNotContain("data-check=\"callers\"", page);
        Assert.Contains("http://&lt;server-ip&gt;:7420/mcp", page);
        Assert.DoesNotContain(LanToken, page);

        foreach (var id in new[] { "connector", "tunnel", "signin" })
            Assert.Equal("Skip", (await s.CheckAsync(id)).GetProperty("status").GetString());
        var lan = await s.CheckAsync("lan");
        Assert.Equal("Ok", lan.GetProperty("status").GetString());
        var command = lan.GetProperty("copy")[0].GetProperty("value").GetString()!;
        Assert.Equal("claude mcp add --transport http unifi http://<server-ip>:7420/mcp --header \"Authorization: Bearer <MCP_LAN_ACCESS_TOKEN>\"", command);
        Assert.DoesNotContain(LanToken, lan.GetRawText());
    }

    [Fact]
    public async Task A_lan_listener_on_loopback_is_a_warning()
    {
        await using var s = await StartAsync(configured: false, tweak: x => Lan(x, "http://127.0.0.1:7420"));
        await s.UnlockAsync();
        var lan = await s.CheckAsync("lan");
        Assert.Equal("Warn", lan.GetProperty("status").GetString());
        Assert.Contains("http://127.0.0.1:7420/mcp", lan.GetProperty("copy")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Next_to_the_connector_it_is_a_fifth_port()
    {
        await using var s = await StartAsync(tweak: x => Lan(x));
        await s.UnlockAsync();
        var page = await s.Http.GetStringAsync("/setup");
        Assert.Contains("data-guide=\"2\"", page);
        Assert.Contains("data-check=\"callers\"", page);
        Assert.Contains("data-check=\"lan\"", page);
        Assert.Contains("bank-ports wide", page);
        Assert.Contains("Claude Code over the LAN", page);
    }

    [Fact]
    public async Task Without_a_lan_listener_the_check_is_not_used()
    {
        await using var s = await StartAsync();
        await s.UnlockAsync();
        Assert.DoesNotContain("data-check=\"lan\"", await s.Http.GetStringAsync("/setup"));
        Assert.Equal("Skip", (await s.CheckAsync("lan")).GetProperty("status").GetString());
    }
}
