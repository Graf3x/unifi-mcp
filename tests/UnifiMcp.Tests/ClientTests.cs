namespace UnifiMcp.Tests;

public class ClientTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-client").FullName;
    private readonly FakeUnifi _fake = new();

    private UnifiClient Client(bool writes = false, bool key = true, bool pin = true, PinState? state = null) =>
        new(Harness.NewSettings(_dir, writes, key, pin), _fake, state ?? new PinState());

    [Fact]
    public async Task An_untrusted_certificate_in_ca_mode_names_the_host_and_the_reason()
    {
        var state = new PinState { Rejected = new("FEEDFACE", "CAFEBABE", System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) };
        _fake.Throw = new HttpRequestException("handshake failed");
        var client = new UnifiClient(Harness.NewSettings(_dir) with { Trust = CertTrust.Ca }, _fake, state);
        var ex = await Assert.ThrowsAsync<UnifiError>(() => client.SendAsync(HttpMethod.Get, "/v1/info", null, default));
        Assert.Contains("not trusted (RemoteCertificateNameMismatch)", ex.Message);
        Assert.Contains("UNIFI_CERT_TRUST=ca", ex.Message);
        Assert.Contains(Harness.NewSettings(_dir).BaseUrl.Host, ex.Message);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Nothing_is_sent_until_key_and_pin_are_configured(bool key, bool pin)
    {
        var ex = await Assert.ThrowsAsync<UnifiNotConfigured>(() => Client(key: key, pin: pin).SendAsync(HttpMethod.Get, "/v1/info", null, default));
        Assert.Contains("not configured", ex.Message);
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task Sends_the_key_header_to_the_integration_api_over_https()
    {
        _fake.Route("GET", "/v1/info", """{"applicationVersion":"11.0.97"}""");
        var r = await Client().SendAsync(HttpMethod.Get, "/v1/info", null, default);
        Assert.True(r.Ok);
        var seen = Assert.Single(_fake.Requests);
        Assert.Equal(Harness.ApiKey, seen.ApiKey);
        Assert.Equal("https", seen.Scheme);
        Assert.Equal("192.168.1.1", seen.Host);
        Assert.Equal("/v1/info", seen.Path);
    }

    [Fact]
    public async Task A_write_is_refused_while_the_flag_is_off()
    {
        await Assert.ThrowsAsync<UnifiError>(() => Client().SendAsync(HttpMethod.Post, "/v1/sites/x/dns/policies", "{}", default));
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task A_console_error_that_echoes_the_key_is_scrubbed()
    {
        _fake.Route("GET", "/v1/info", $$"""{"message":"bad key {{Harness.ApiKey}}"}""", 401);
        var r = await Client().SendAsync(HttpMethod.Get, "/v1/info", null, default);
        Assert.False(r.Ok);
        Assert.DoesNotContain(Harness.ApiKey, r.Body);
    }

    [Fact]
    public async Task A_transport_failure_never_carries_the_key()
    {
        _fake.Throw = new HttpRequestException($"boom {Harness.ApiKey}");
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client().SendAsync(HttpMethod.Get, "/v1/info", null, default));
        Assert.DoesNotContain(Harness.ApiKey, ex.Message);
    }

    [Fact]
    public async Task A_pin_mismatch_names_the_fingerprint_the_console_presented()
    {
        var state = new PinState { Rejected = new("FEEDFACE", "CAFEBABE") };
        _fake.Throw = new HttpRequestException("handshake failed");
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client(state: state).SendAsync(HttpMethod.Get, "/v1/info", null, default));
        Assert.Contains("FEEDFACE", ex.Message);
        Assert.Contains("CAFEBABE", ex.Message);
        Assert.Contains("pin", ex.Message);
        // Where to change it: the environment variable itself (fleet-key at most as an example), else the file.
        Assert.Matches(@"(?<!fleet-key )set UNIFI_CERT_SHA256", ex.Message);
        Assert.Contains(Path.Combine(_dir, "pin"), ex.Message);
    }

    [Fact]
    public async Task A_site_uuid_passes_through_without_a_lookup()
    {
        Assert.Equal(Harness.SiteId, await Client().ResolveSiteAsync(Harness.SiteId, default));
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task A_site_name_or_reference_resolves_to_its_id()
    {
        _fake.WithSite();
        Assert.Equal(Harness.SiteId, await Client().ResolveSiteAsync("Default", default));
        Assert.Equal(Harness.SiteId, await Client().ResolveSiteAsync(null, default)); // configured default: "default"
    }

    [Fact]
    public async Task An_unknown_site_lists_the_ones_that_exist()
    {
        _fake.WithSite();
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Client().ResolveSiteAsync("branch", default));
        Assert.Contains("Default", ex.Message);
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
