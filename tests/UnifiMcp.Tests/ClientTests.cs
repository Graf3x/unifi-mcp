namespace UnifiMcp.Tests;

public class ClientTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-client").FullName;
    private readonly FakeUnifi _fake = new();

    private UnifiClient Client(bool writes = false)
    {
        var settings = Harness.NewSettings(_dir, writes);
        return new(settings, new ConsoleTransport(settings, _fake));
    }

    [Fact]
    public async Task The_integration_api_is_reached_under_its_root()
    {
        _fake.Route("GET", "/v1/info", """{"applicationVersion":"11.0.97"}""");
        Assert.True((await Client().SendAsync(HttpMethod.Get, "/v1/info", null, default)).Ok);
        Assert.Equal("/v1/info", Assert.Single(_fake.Requests).Path); // FakeUnifi strips /proxy/network/integration
    }

    [Fact]
    public async Task A_write_is_refused_while_the_flag_is_off()
    {
        await Assert.ThrowsAsync<UnifiError>(() => Client().SendAsync(HttpMethod.Post, "/v1/sites/x/dns/policies", "{}", default));
        Assert.Empty(_fake.Requests);
    }

    [Theory]
    [InlineData(Harness.SiteId)]
    [InlineData("Default")]
    [InlineData("DEFAULT")] // the internal name, any case
    [InlineData(null)]      // the configured site: "default"
    public async Task A_site_is_found_by_its_id_name_or_internal_name(string? hint)
    {
        _fake.WithSite();
        Assert.Equal(new Site(Harness.SiteId, "default", "Default"), await Client().FindSiteAsync(hint, default));
    }

    [Fact]
    public async Task A_site_id_wins_over_another_site_named_after_it()
    {
        const string Other = "22222222-2222-2222-2222-222222222222";
        // The odd site comes first, and its name and internal name are the other site's id.
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(
            $$"""{"id":"{{Harness.SiteId}}","internalReference":"{{Other}}","name":"{{Other}}"}""",
            $$"""{"id":"{{Other}}","internalReference":"cabin","name":"Cabin"}"""));
        Assert.Equal(Other, (await Client().FindSiteAsync(Other, default)).Id);
    }

    [Fact]
    public async Task A_site_uuid_the_console_does_not_have_lists_the_ones_that_exist()
    {
        _fake.WithSite();
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client().FindSiteAsync("22222222-2222-2222-2222-222222222222", default));
        Assert.Contains("no site '22222222-2222-2222-2222-222222222222'", ex.Message);
        Assert.Contains("Default", ex.Message);
    }

    [Fact]
    public async Task An_unknown_site_lists_the_ones_that_exist()
    {
        _fake.WithSite();
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client().FindSiteAsync("branch", default));
        Assert.Contains("Default", ex.Message);
    }

    [Fact]
    public async Task A_site_is_looked_up_once_however_it_is_named_again()
    {
        _fake.WithSite();
        var client = Client();
        await client.FindSiteAsync("Default", default);
        await client.FindSiteAsync("Default", default);
        await client.FindSiteAsync(null, default);
        Assert.Single(_fake.Requests, r => r.Path.StartsWith("/v1/sites", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_answer_of_an_unexpected_shape_is_a_clear_error()
    {
        _fake.Route("GET", "/v1/items?offset=0&limit=200", """{"count":0}""");
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client().GetAllAsync("/v1/items", default));
        Assert.Contains("/v1/items", ex.Message);
    }

    [Fact]
    public async Task GetAll_walks_every_page()
    {
        _fake.Route("GET", "/v1/items?offset=0&limit=200", """{"offset":0,"limit":200,"count":2,"totalCount":3,"data":[{"n":1},{"n":2}]}""");
        _fake.Route("GET", "/v1/items?offset=2&limit=200", """{"offset":2,"limit":200,"count":1,"totalCount":3,"data":[{"n":3}]}""");
        var all = await Client().GetAllAsync("/v1/items", default);
        Assert.Equal(3, all.Count);
    }
}
