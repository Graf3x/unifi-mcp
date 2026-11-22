using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

/// <summary>
/// The console's internal API through <see cref="ConsoleReads"/>: named reads for a site, cached briefly and throttled,
/// because that API was not built for outside polling and heavy polling has destabilised consoles.
/// </summary>
public sealed class ConsoleReadsTests : IDisposable
{
    private const string Classic = "/proxy/network/api/s/default";
    private const string V2 = "/proxy/network/v2/api/site/default";

    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-console-reads").FullName;
    private readonly FakeUnifi _fake = new();
    private readonly FakeTimeProvider _time = new();
    private readonly UnifiClient _client;
    private readonly ConsoleReads _reads;

    public ConsoleReadsTests()
    {
        _fake.WithSite();
        _fake.Route("GET", $"{Classic}/stat/health", """{"meta":{"rc":"ok"},"data":[{"subsystem":"www"}]}""");
        _fake.Route("POST", $"{V2}/system-log/all", """{"data":[],"total_element_count":0,"total_page_count":0}""");
        var settings = Harness.NewSettings(_dir);
        _client = new UnifiClient(settings, new ConsoleTransport(settings, _fake));
        _reads = new ConsoleReads(_client, _time);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private int InternalReads() => _fake.Requests.Count(r => r.Path.StartsWith("/proxy/network/", StringComparison.Ordinal));

    private static SystemLogQuery Page(int page) => new(0, 1, 50, page, null, null, null, null);

    [Fact]
    public async Task The_same_read_within_a_few_seconds_is_answered_from_cache()
    {
        var site = await _reads.SiteAsync(null, default);
        await site.HealthAsync(default);
        await site.HealthAsync(default);
        Assert.Equal(1, InternalReads());

        _time.Advance(ConsoleReads.CacheFor + TimeSpan.FromSeconds(1));
        await site.HealthAsync(default);
        Assert.Equal(2, InternalReads());
    }

    [Fact]
    public async Task Reads_beyond_the_burst_are_refused_until_the_budget_refills()
    {
        var site = await _reads.SiteAsync(null, default);
        // Different pages, so the cache does not absorb them.
        for (var i = 0; i < ConsoleReads.Burst; i++) await site.SystemLogAsync(Page(i), default);
        var e = await Assert.ThrowsAsync<UnifiError>(() => site.SystemLogAsync(Page(99), default));
        Assert.Contains("try again in", e.Message);
        Assert.Equal(ConsoleReads.Burst, InternalReads());

        _time.Advance(TimeSpan.FromSeconds(60.0 / ConsoleReads.PerMinute + 0.1));
        await site.SystemLogAsync(Page(99), default);
        Assert.Equal(ConsoleReads.Burst + 1, InternalReads());
    }

    [Fact]
    public async Task The_integration_api_is_not_throttled()
    {
        for (var i = 0; i < ConsoleReads.Burst + 5; i++) await _client.SendAsync(HttpMethod.Get, "/v1/sites", null, default);
        Assert.Equal(ConsoleReads.Burst + 5, _fake.Requests.Count);
    }

    [Fact]
    public async Task A_site_is_named_to_the_internal_api_by_its_internal_name()
    {
        var site = await _reads.SiteAsync(Harness.SiteId, default);
        Assert.Equal("default", site.Reference);
        Assert.Equal("Default", site.Name);
        await site.HealthAsync(default);
        Assert.Contains(_fake.Requests, r => r.Path == $"{Classic}/stat/health");
    }

    [Fact]
    public async Task Each_site_reads_in_its_own_time_zone()
    {
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page(
            $$"""{"id":"{{Harness.SiteId}}","internalReference":"default","name":"Home"}""",
            """{"id":"22222222-2222-2222-2222-222222222222","internalReference":"cabin","name":"Cabin"}"""));
        _fake.Route("GET", $"{Classic}/stat/sysinfo", """{"meta":{"rc":"ok"},"data":[{"timezone":"Europe/London"}]}""");
        _fake.Route("GET", "/proxy/network/api/s/cabin/stat/sysinfo", """{"meta":{"rc":"ok"},"data":[{"timezone":"America/Denver"}]}""");

        Assert.Equal("Europe/London", (await (await _reads.SiteAsync("Home", default)).TimeZoneAsync(default)).Name);
        Assert.Equal("America/Denver", (await (await _reads.SiteAsync("Cabin", default)).TimeZoneAsync(default)).Name);
        Assert.Equal("Europe/London", (await (await _reads.SiteAsync("Home", default)).TimeZoneAsync(default)).Name);
    }

    [Fact]
    public async Task A_known_time_zone_is_not_asked_for_again_but_an_unknown_one_is()
    {
        var site = await _reads.SiteAsync(null, default);
        Assert.Equal("UTC", (await site.TimeZoneAsync(default)).Name); // no sysinfo route: the fake answers 404
        _fake.Route("GET", $"{Classic}/stat/sysinfo", """{"meta":{"rc":"ok"},"data":[{"timezone":"Europe/London"}]}""");
        _time.Advance(ConsoleReads.CacheFor + TimeSpan.FromSeconds(1));
        Assert.Equal("Europe/London", (await site.TimeZoneAsync(default)).Name);

        _time.Advance(ConsoleReads.CacheFor + TimeSpan.FromSeconds(1));
        Assert.Equal("Europe/London", (await site.TimeZoneAsync(default)).Name);
        Assert.Equal(2, _fake.Requests.Count(r => r.Path.EndsWith("/stat/sysinfo", StringComparison.Ordinal)));
    }

    [Fact]
    public void No_caller_can_choose_the_path_or_method_of_an_internal_read()
    {
        // The named reads are the allow-list: the only string a caller passes is the site hint, which is looked up.
        static IEnumerable<System.Reflection.ParameterInfo> Parameters(Type t) =>
            t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
             .Where(m => !m.IsSpecialName).SelectMany(m => m.GetParameters().Select(p => p));
        Assert.Equal(["hint"], Parameters(typeof(ConsoleReads)).Where(p => p.ParameterType == typeof(string)).Select(p => p.Name));
        Assert.DoesNotContain(Parameters(typeof(ConsoleSite)), p => p.ParameterType == typeof(string) || p.ParameterType == typeof(HttpMethod));
        Assert.DoesNotContain(Parameters(typeof(ConsoleReads)), p => p.ParameterType == typeof(HttpMethod));
    }

    [Fact]
    public async Task A_site_whose_internal_name_the_internal_api_cannot_take_is_refused_before_any_read()
    {
        _fake.Route("GET", "/v1/sites", FakeUnifi.Page($$"""{"id":"{{Harness.SiteId}}","internalReference":"../cmd","name":"Odd"}"""));
        var e = await Assert.ThrowsAsync<UnifiError>(() => _reads.SiteAsync("Odd", default));
        Assert.Contains("site 'Odd'", e.Message);
        Assert.Equal(0, InternalReads());
    }
}
