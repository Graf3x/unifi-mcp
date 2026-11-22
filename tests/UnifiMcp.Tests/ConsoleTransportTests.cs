using System.Security.Cryptography;

namespace UnifiMcp.Tests;

/// <summary>
/// <see cref="ConsoleTransport"/>: how a request reaches the console. Trust is tested with real handshakes against a
/// <see cref="LoopbackConsole"/>; what happens above TLS, with <see cref="FakeUnifi"/>.
/// </summary>
public sealed class ConsoleTransportTests : IDisposable
{
    private const string Info = FakeUnifi.Prefix + "/v1/info";

    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-transport").FullName;
    private readonly FakeUnifi _fake = new();

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Settings for the loopback console: no pin file, so only <paramref name="pin"/> counts.</summary>
    private Settings For(LoopbackConsole console, string? pin, CertTrust trust = CertTrust.Pin) =>
        Harness.NewSettings(_dir, pin: false) with { BaseUrl = console.Url, CertSha256 = pin, Trust = trust };

    private static Task<UnifiResponse> Get(ConsoleTransport transport, string path = Info) => transport.SendAsync(HttpMethod.Get, path, null, default);

    [Fact]
    public async Task The_pinned_certificate_lets_the_request_through_with_the_key()
    {
        using var cert = LoopbackConsole.NewCertificate();
        await using var console = await LoopbackConsole.StartAsync(cert);
        var r = await Get(new ConsoleTransport(For(console, LoopbackConsole.CertificateSha256(cert))));
        Assert.True(r.Ok);
        Assert.Equal([Harness.ApiKey], console.Keys);
    }

    [Fact]
    public async Task A_key_pin_survives_a_reissued_certificate_that_keeps_its_key()
    {
        using var rsa = RSA.Create(2048);
        using var before = LoopbackConsole.NewCertificate("CN=udm", rsa);
        using var after = LoopbackConsole.NewCertificate("CN=udm-renewed", rsa);
        await using var console = await LoopbackConsole.StartAsync(after);
        Assert.True((await Get(new ConsoleTransport(For(console, LoopbackConsole.KeySha256(before))))).Ok);
    }

    [Fact]
    public async Task A_certificate_off_the_pin_is_refused_named_and_never_sent_the_key()
    {
        using var presented = LoopbackConsole.NewCertificate();
        using var pinned = LoopbackConsole.NewCertificate();
        await using var console = await LoopbackConsole.StartAsync(presented);
        var settings = For(console, LoopbackConsole.CertificateSha256(pinned));

        var ex = await Assert.ThrowsAsync<UnifiError>(() => Get(new ConsoleTransport(settings)));
        Assert.Contains("does not match the pin", ex.Message);
        Assert.Contains(LoopbackConsole.CertificateSha256(presented), ex.Message);
        Assert.Contains(LoopbackConsole.KeySha256(presented), ex.Message);
        // Where to change it: the environment variable itself (fleet-key at most as an example), else the file.
        Assert.Matches(@"(?<!fleet-key )set UNIFI_CERT_SHA256", ex.Message);
        Assert.Contains(settings.CertPinFile, ex.Message);
        Assert.Empty(console.Keys);
    }

    [Fact]
    public async Task Ca_mode_refuses_a_self_signed_certificate_and_says_why()
    {
        using var cert = LoopbackConsole.NewCertificate();
        await using var console = await LoopbackConsole.StartAsync(cert);
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Get(new ConsoleTransport(For(console, pin: null, CertTrust.Ca))));
        Assert.Contains("not trusted (", ex.Message);
        Assert.Contains("RemoteCertificateChainErrors", ex.Message);
        Assert.Contains("UNIFI_CERT_TRUST=ca", ex.Message);
        Assert.Contains(console.Url.Host, ex.Message);
        Assert.Contains(LoopbackConsole.CertificateSha256(cert), ex.Message);
        Assert.Empty(console.Keys);
    }

    [Fact]
    public async Task A_redirect_is_handed_back_never_followed()
    {
        using var cert = LoopbackConsole.NewCertificate();
        await using var console = await LoopbackConsole.StartAsync(cert);
        var r = await Get(new ConsoleTransport(For(console, LoopbackConsole.CertificateSha256(cert))), FakeUnifi.Prefix + "/v1/moved");
        Assert.Equal(302, r.Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Nothing_is_sent_until_key_and_pin_are_configured(bool key, bool pin)
    {
        var transport = new ConsoleTransport(Harness.NewSettings(_dir, key: key, pin: pin), _fake);
        var ex = await Assert.ThrowsAsync<UnifiNotConfigured>(() => Get(transport));
        Assert.Contains("not configured", ex.Message);
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task The_key_goes_in_a_header_to_the_console_over_https()
    {
        _fake.Route("GET", "/v1/info", """{"applicationVersion":"11.0.97"}""");
        var r = await Get(new ConsoleTransport(Harness.NewSettings(_dir), _fake));
        Assert.True(r.Ok);
        var seen = Assert.Single(_fake.Requests);
        Assert.Equal(Harness.ApiKey, seen.ApiKey);
        Assert.Equal("https", seen.Scheme);
        Assert.Equal("192.168.1.1", seen.Host);
        Assert.Equal("/v1/info", seen.Path);
    }

    [Fact]
    public async Task A_console_answer_that_echoes_the_key_is_scrubbed()
    {
        _fake.Route("GET", "/v1/info", $$"""{"message":"bad key {{Harness.ApiKey}}"}""", 401);
        var r = await Get(new ConsoleTransport(Harness.NewSettings(_dir), _fake));
        Assert.False(r.Ok);
        Assert.DoesNotContain(Harness.ApiKey, r.Body);
    }

    [Fact]
    public async Task A_transport_failure_never_carries_the_key()
    {
        _fake.Throw = new HttpRequestException($"boom {Harness.ApiKey}");
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Get(new ConsoleTransport(Harness.NewSettings(_dir), _fake)));
        Assert.Contains("Could not reach the console", ex.Message);
        Assert.DoesNotContain(Harness.ApiKey, ex.Message);
    }

    [Fact]
    public async Task A_console_that_does_not_answer_in_time_says_so()
    {
        _fake.Throw = new TaskCanceledException("timed out");
        var ex = await Assert.ThrowsAsync<UnifiError>(() => Get(new ConsoleTransport(Harness.NewSettings(_dir), _fake)));
        Assert.Contains("did not answer in time", ex.Message);
    }
}
