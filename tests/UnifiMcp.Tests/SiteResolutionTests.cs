using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

/// <summary>
/// Which site a name means: an id first, a name or internal name only when one site has it, and for a write the
/// console's current list, so a renamed site cannot send a change to the site that held the name before.
/// </summary>
public class SiteResolutionTests
{
    private const string A = Harness.SiteId;
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string Tool = "create_dns_policy";

    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-sites").FullName;
    private readonly FakeUnifi _fake = new();

    private UnifiClient Client(bool writes = false)
    {
        var settings = Harness.NewSettings(_dir, writes);
        return new(settings, new ConsoleTransport(settings, _fake));
    }

    private static string Site(string id, string reference, string name) =>
        $$"""{"id":"{{id}}","internalReference":"{{reference}}","name":"{{name}}"}""";

    /// <summary>A is Cabin and B is Lodge.</summary>
    private static string Before => FakeUnifi.Page(Site(A, "default", "Cabin"), Site(B, "b7kq2m1p", "Lodge"));

    /// <summary>A was renamed Barn, and B took the name Cabin.</summary>
    private static string After => FakeUnifi.Page(Site(A, "default", "Barn"), Site(B, "b7kq2m1p", "Cabin"));

    private static string PostPath(string site) => $"/v1/sites/{site}/dns/policies";

    private static int Posts(FakeUnifi fake) => fake.Requests.Count(r => r.Method == "POST");

    private static Dictionary<string, object?> Write(string site, string? confirm = null)
    {
        var args = new Dictionary<string, object?> { ["siteId"] = site, ["body"] = new { type = "A_RECORD", domain = "x.test" } };
        if (confirm is not null) args["confirm"] = confirm;
        return args;
    }

    private static async Task<(string Text, bool IsError)> CallAsync(McpClient mcp, string tool, Dictionary<string, object?> args)
    {
        var r = await mcp.CallToolAsync(tool, args);
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    [Fact]
    public async Task Two_sites_with_the_same_name_are_refused_with_both_ids()
    {
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(Site(A, "default", "Office"), Site(B, "b7kq2m1p", "office")));

        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client().FindSiteAsync("Office", default));

        Assert.Contains("More than one site", ex.Message);
        Assert.Contains(A, ex.Message);
        Assert.Contains(B, ex.Message);
        Assert.Contains("office", ex.Message);
    }

    [Fact]
    public async Task A_name_that_is_another_sites_internal_name_is_refused()
    {
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(Site(A, "default", "Cabin"), Site(B, "b7kq2m1p", "Default")));

        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client().FindSiteAsync("default", default));

        Assert.Contains(A, ex.Message);
        Assert.Contains(B, ex.Message);
    }

    [Fact]
    public async Task An_id_still_wins_when_names_clash()
    {
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(Site(A, "default", "Office"), Site(B, "b7kq2m1p", "Office")));
        Assert.Equal(B, (await Client().FindSiteAsync(B, write: true, default)).Id);
    }

    [Fact]
    public async Task An_id_wins_over_an_earlier_site_whose_name_is_that_id()
    {
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(Site(A, "default", B), Site(B, "b7kq2m1p", "Lodge")));
        Assert.Equal(B, (await Client().FindSiteAsync(B, default)).Id);
    }

    [Fact]
    public async Task An_id_wins_over_an_earlier_site_whose_internal_name_is_that_id()
    {
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(Site(A, B, "Cabin"), Site(B, "b7kq2m1p", "Lodge")));
        Assert.Equal(B, (await Client().FindSiteAsync(B, default)).Id);
    }

    // --- a console with more sites than this server reads -----------------------------------------------------------

    /// <summary>
    /// Pages of the site list for a console with one site more than <see cref="UnifiClient.MaxItems"/>: <paramref name="first"/>
    /// at the front, then numbered sites. The last site, past what this server reads, is never served.
    /// </summary>
    private void ManySites(params string[] first)
    {
        const int Total = UnifiClient.MaxItems + 1;
        for (var offset = 0; offset < UnifiClient.MaxItems; offset += 200)
        {
            var items = Enumerable.Range(offset, 200).Select(i => i < first.Length ? first[i] : Site($"00000000-0000-0000-0000-{i:D12}", $"r{i}", $"Site {i}"));
            _fake.Route("GET", $"/v1/sites?offset={offset}&limit=200",
                $$"""{"offset":{{offset}},"limit":200,"count":200,"totalCount":{{Total}},"data":[{{string.Join(",", items)}}]}""");
        }
    }

    [Fact]
    public async Task A_name_is_not_resolved_from_a_site_list_cut_short()
    {
        // B, past the sites this server reads, has an id that is A's name: a lookup of B's id must not land on A.
        ManySites(Site(A, "default", B));

        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client(writes: true).FindSiteAsync(B, write: true, default));

        Assert.Contains($"{UnifiClient.MaxItems}", ex.Message);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public async Task A_unique_name_in_a_site_list_cut_short_is_refused_too()
    {
        // Another site called Cabin may be past the cut, so the name is not known to be unique.
        ManySites(Site(A, "default", "Cabin"));
        await Assert.ThrowsAsync<UnifiError>(() => Client().FindSiteAsync("Cabin", default));
    }

    [Fact]
    public async Task An_id_found_in_a_site_list_cut_short_is_still_used()
    {
        ManySites(Site(B, "b7kq2m1p", "Lodge"), Site(A, "default", B));
        Assert.Equal(B, (await Client().FindSiteAsync(B, default)).Id);
    }

    [Fact]
    public async Task A_write_looks_a_name_up_again_and_reads_then_see_the_new_answer()
    {
        _fake.Route("GET", "/v1/sites", Before);
        var client = Client(writes: true);
        Assert.Equal(A, (await client.FindSiteAsync("Cabin", default)).Id);

        _fake.Route("GET", "/v1/sites", After);
        Assert.Equal(A, (await client.FindSiteAsync("Cabin", default)).Id); // a read may use what it looked up before
        Assert.Equal(B, (await client.FindSiteAsync("Cabin", write: true, default)).Id);
        Assert.Equal(B, (await client.FindSiteAsync("Cabin", default)).Id);
    }

    [Fact]
    public async Task A_write_by_name_is_refused_once_the_name_becomes_ambiguous()
    {
        _fake.Route("GET", "/v1/sites", Before);
        var client = Client(writes: true);
        await client.FindSiteAsync("Cabin", default);

        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(Site(A, "default", "Cabin"), Site(B, "b7kq2m1p", "Cabin")));

        await Assert.ThrowsAsync<UnifiError>(() => client.FindSiteAsync("Cabin", write: true, default));
    }

    [Fact]
    public async Task A_write_by_id_needs_no_second_lookup()
    {
        _fake.Route("GET", "/v1/sites", Before);
        var client = Client(writes: true);
        await client.FindSiteAsync(A, write: true, default);
        await client.FindSiteAsync(A, write: true, default);
        Assert.Single(_fake.Requests, r => r.Path.StartsWith("/v1/sites", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_the_host_an_ambiguous_name_asks_nothing_and_sends_nothing()
    {
        await using var h = await Harness.StartAsync(writes: true);
        h.Unifi.Route("GET", "/v1/sites", FakeUnifi.Page(Site(A, "default", "Office"), Site(B, "b7kq2m1p", "Office")));
        var asked = 0;
        await using var mcp = await h.ConnectAsync(elicit: _ => { asked++; return "accept"; });

        var (formText, formError) = await CallAsync(mcp, Tool, Write("Office"));
        var (confirmText, confirmError) = await h.CallAsync(Tool, Write("Office", confirm: Tool));
        var (readText, readError) = await h.CallAsync("call_read_operation", new()
        {
            ["operation"] = "get_networks_overview_page",
            ["arguments"] = new Dictionary<string, object?> { ["siteId"] = "Office" },
        });

        Assert.True(formError, formText);
        Assert.Contains(A, formText);
        Assert.Contains(B, formText);
        Assert.True(confirmError, confirmText);
        Assert.True(readError, readText);
        Assert.Equal(0, asked);
        Assert.Equal(0, Posts(h.Unifi));
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Path.StartsWith("/v1/sites/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_the_host_a_write_after_a_rename_goes_to_the_site_that_has_the_name_now()
    {
        await using var h = await Harness.StartAsync(writes: true, confirmFallback: true);
        h.Unifi.Route("GET", "/v1/sites", Before)
            .Route("GET", $"/v1/sites/{A}/networks", FakeUnifi.Page())
            .Route("POST", PostPath(A), """{"id":"wrong"}""", 201)
            .Route("POST", PostPath(B), """{"id":"new"}""", 201);
        await h.CallAsync("call_read_operation", new()
        {
            ["operation"] = "get_networks_overview_page",
            ["arguments"] = new Dictionary<string, object?> { ["siteId"] = "Cabin" },
        });

        h.Unifi.Route("GET", "/v1/sites", After);
        var (text, isError) = await h.CallAsync(Tool, Write("Cabin", confirm: Tool));

        Assert.False(isError, text);
        Assert.Contains("new", text);
        Assert.DoesNotContain(h.Unifi.Requests, r => r.Method == "POST" && r.Path == PostPath(A));
        Assert.Single(h.Unifi.Requests, r => r.Method == "POST" && r.Path == PostPath(B));
    }

    [Fact]
    public async Task On_the_host_a_rename_while_the_form_is_open_needs_a_new_decision()
    {
        await using var h = await Harness.StartAsync(writes: true);
        h.Unifi.Route("GET", "/v1/sites", Before)
            .Route("POST", PostPath(A), """{"id":"wrong"}""", 201)
            .Route("POST", PostPath(B), """{"id":"new"}""", 201);
        var forms = new List<string>();
        await using var mcp = await h.ConnectAsync(elicit: p =>
        {
            forms.Add(p.Message);
            if (forms.Count > 1) return "decline";
            h.Unifi.Route("GET", "/v1/sites", After); // the owner approves A, and meanwhile Cabin comes to mean B
            return "accept";
        });

        var (text, isError) = await CallAsync(mcp, Tool, Write("Cabin"));

        Assert.False(isError, text);
        Assert.Contains("declined", text);
        Assert.Equal(2, forms.Count);
        Assert.Contains($"POST {PostPath(A)}", forms[0]);
        Assert.Contains($"POST {PostPath(B)}", forms[1]); // the second form names the site the request now goes to
        Assert.Equal(0, Posts(h.Unifi));
    }
}
