using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace UnifiMcp.Tests;

/// <summary>
/// Secrets the console sends (Wi-Fi passphrases, per-network keys, voucher codes) are redacted before an answer reaches
/// the client, in reads, in echoed writes and in error bodies. Each secret is a distinct synthetic sentinel, so a test
/// failure names the one that leaked.
/// </summary>
public sealed class RedactionTests : IDisposable
{
    private const string Passphrase = "SENTINEL-wifi-passphrase-7f3a";
    private const string NetworkKey = "SENTINEL-network-key-91c2";
    private const string VoucherCode = "SENTINEL-voucher-4455";
    private const string BroadcastId = "44444444-4444-4444-4444-444444444444";
    private const string NetworkId = "55555555-5555-5555-5555-555555555555";
    private static readonly string BroadcastPath = $"/v1/sites/{Harness.SiteId}/wifi/broadcasts/{BroadcastId}";

    /// <summary>A WPA2 personal broadcast as the document describes it, with the API key smuggled into a field as well.</summary>
    private static readonly string Broadcast = $$"""
        {"type":"STANDARD","id":"{{BroadcastId}}","name":"Home Wi-Fi","enabled":true,"hideName":false,
         "network":{"type":"SPECIFIC","networkId":"{{NetworkId}}"},
         "note":"{{Harness.ApiKey}}",
         "securityConfiguration":{"type":"WPA2_PERSONAL","pmfMode":"OPTIONAL","fastRoamingEnabled":true,
           "passphrase":"{{Passphrase}}",
           "presharedKeys":[{"network":{"type":"SPECIFIC","networkId":"{{NetworkId}}"},"passphrase":"{{NetworkKey}}"}] } }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-redaction").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain(Harness.ApiKey, text);
        Assert.DoesNotContain(Passphrase, text);
        Assert.DoesNotContain(NetworkKey, text);
        Assert.DoesNotContain(VoucherCode, text);
    }

    [Fact]
    public async Task A_wifi_broadcast_read_shows_its_settings_but_no_passphrase_or_key()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite().Route("GET", BroadcastPath, Broadcast);

        var (text, isError) = await h.CallAsync(ReadOperationTool.ToolName, new()
        {
            ["operation"] = "get_wifi_broadcast_details",
            ["arguments"] = new Dictionary<string, object?> { ["wifiBroadcastId"] = BroadcastId },
        });

        Assert.False(isError, text);
        AssertNoSecrets(text);
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Home Wi-Fi", json.GetProperty("name").GetString());
        var security = json.GetProperty("securityConfiguration");
        Assert.Equal("WPA2_PERSONAL", security.GetProperty("type").GetString());
        Assert.Equal("OPTIONAL", security.GetProperty("pmfMode").GetString());
        Assert.True(security.GetProperty("fastRoamingEnabled").GetBoolean());
        Assert.Equal(Redaction.Mark, security.GetProperty("passphrase").GetString());
        var key = Assert.Single(security.GetProperty("presharedKeys").EnumerateArray().ToList());
        Assert.Equal(NetworkId, key.GetProperty("network").GetProperty("networkId").GetString()); // which network it is for stays
        Assert.Equal(Redaction.Mark, key.GetProperty("passphrase").GetString());
    }

    [Fact]
    public async Task A_list_of_broadcasts_is_redacted_in_every_item()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite().Route("GET", $"/v1/sites/{Harness.SiteId}/wifi/broadcasts", FakeUnifi.Page(Broadcast, Broadcast));

        var (text, isError) = await h.CallAsync(ReadOperationTool.ToolName, new() { ["operation"] = "get_wifi_broadcast_page" });

        Assert.False(isError, text);
        AssertNoSecrets(text);
        Assert.Contains("Home Wi-Fi", text);
    }

    [Fact]
    public async Task A_write_that_echoes_the_broadcast_back_is_redacted()
    {
        await using var h = await Harness.StartAsync(writes: true, confirmFallback: true);
        h.Unifi.WithSite().Route("PUT", BroadcastPath, Broadcast);

        var (text, isError) = await h.CallAsync("update_wifi_broadcast", new()
        {
            ["wifiBroadcastId"] = BroadcastId,
            ["body"] = new { name = "Home Wi-Fi" },
            ["confirm"] = "update_wifi_broadcast",
        });

        Assert.False(isError, text);
        AssertNoSecrets(text);
        Assert.Contains("Home Wi-Fi", text);
    }

    [Fact]
    public async Task An_error_body_is_redacted_and_keeps_its_message()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite().Route("GET", BroadcastPath,
            $$"""{"statusCode":400,"code":"api.wifi.invalid","message":"bad","rejected":{"passphrase":"{{Passphrase}}","presharedKeys":["{{NetworkKey}}"]} }""", 400);

        var (text, isError) = await h.CallAsync(ReadOperationTool.ToolName, new()
        {
            ["operation"] = "get_wifi_broadcast_details",
            ["arguments"] = new Dictionary<string, object?> { ["wifiBroadcastId"] = BroadcastId },
        });

        Assert.True(isError);
        AssertNoSecrets(text);
        Assert.Contains("api.wifi.invalid", text);
        Assert.Contains("answered 400", text);
    }

    [Fact]
    public async Task An_error_body_that_is_not_json_is_not_shown()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite().Route("GET", BroadcastPath, $"<html>passphrase={Passphrase}</html>", 502);

        var (text, isError) = await h.CallAsync(ReadOperationTool.ToolName, new()
        {
            ["operation"] = "get_wifi_broadcast_details",
            ["arguments"] = new Dictionary<string, object?> { ["wifiBroadcastId"] = BroadcastId },
        });

        Assert.True(isError);
        AssertNoSecrets(text);
        Assert.Contains("answered 502", text);
        Assert.Contains("not JSON", text);
    }

    [Fact]
    public async Task A_success_body_that_is_not_json_is_not_shown()
    {
        await using var h = await Harness.StartAsync();
        h.Unifi.WithSite().Route("GET", BroadcastPath, $"passphrase={Passphrase}");

        var (text, isError) = await h.CallAsync(ReadOperationTool.ToolName, new()
        {
            ["operation"] = "get_wifi_broadcast_details",
            ["arguments"] = new Dictionary<string, object?> { ["wifiBroadcastId"] = BroadcastId },
        });

        Assert.True(isError);
        AssertNoSecrets(text);
        Assert.Contains("not JSON", text);
    }

    [Fact]
    public async Task Voucher_codes_are_redacted_unless_the_deployment_reveals_them()
    {
        await using var h = await Harness.StartAsync();
        var voucher = $$"""{"id":"66666666-6666-6666-6666-666666666666","name":"Guest","code":"{{VoucherCode}}","expired":false,"timeLimitMinutes":60}""";
        h.Unifi.WithSite().Route("GET", $"/v1/sites/{Harness.SiteId}/hotspot/vouchers", FakeUnifi.Page(voucher));

        var (text, isError) = await h.CallAsync(ReadOperationTool.ToolName, new() { ["operation"] = "get_vouchers" });

        Assert.False(isError, text);
        AssertNoSecrets(text);
        Assert.Contains("Guest", text);
        Assert.Contains("timeLimitMinutes", text);

        var op = OpenApiCatalog.Load().Find("get_vouchers")!;
        var revealed = Redaction.Answer(op, FakeUnifi.Page(voucher), new Settings { RevealVoucherCodes = true });
        Assert.Contains(VoucherCode, revealed);
    }

    [Fact]
    public async Task Created_voucher_codes_are_redacted_by_default()
    {
        await using var h = await Harness.StartAsync(writes: true, confirmFallback: true);
        h.Unifi.WithSite().Route("POST", $"/v1/sites/{Harness.SiteId}/hotspot/vouchers",
            $$"""{"vouchers":[{"id":"66666666-6666-6666-6666-666666666666","name":"Guest","code":"{{VoucherCode}}"}]}""", 201);

        var (text, isError) = await h.CallAsync("create_vouchers", new()
        {
            ["body"] = new { count = 1, name = "Guest", timeLimitMinutes = 60 },
            ["confirm"] = "create_vouchers",
        });

        Assert.False(isError, text);
        AssertNoSecrets(text);
        Assert.Contains("Guest", text);
    }

    [Fact]
    public void Revealing_voucher_codes_never_reveals_a_wifi_passphrase()
    {
        var op = OpenApiCatalog.Load().Find("get_wifi_broadcast_details")!;
        var shown = Redaction.Answer(op, Broadcast, new Settings { RevealVoucherCodes = true })!;
        Assert.DoesNotContain(Passphrase, shown);
        Assert.DoesNotContain(NetworkKey, shown);
    }

    [Fact]
    public void An_error_code_field_is_kept_outside_voucher_answers()
    {
        // "code" is a secret only where the document says so (a voucher); an error's code is what explains it.
        Assert.Contains("api.err.Invalid", Redaction.Error("""{"code":"api.err.Invalid"}"""));
    }

    [Theory]
    [InlineData("""{"x_passphrase":"SENTINEL-wifi-passphrase-7f3a","name":"Home"}""")]
    [InlineData("""{"data":[{"private_preshared_keys":[{"password":"SENTINEL-network-key-91c2","networkconf_id":"n1"}]}]}""")]
    [InlineData("""{"presharedKeys":["SENTINEL-network-key-91c2"]}""")]
    [InlineData("""{"radius":{"x_secret":"SENTINEL-network-key-91c2"}}""")]
    [InlineData("""{"wpa_psk":"SENTINEL-wifi-passphrase-7f3a"}""")]
    [InlineData("""{"passphrase":{"value":"SENTINEL-wifi-passphrase-7f3a"}}""")]
    public void Secret_fields_are_redacted_wherever_they_are_nested(string body)
    {
        var shown = Redaction.Error(body);
        AssertNoSecrets(shown);
        Assert.Contains(Redaction.Mark, shown);
    }

    [Fact]
    public void An_error_body_that_is_not_json_becomes_a_fixed_line()
    {
        var shown = Redaction.Error($"Internal error: passphrase {Passphrase}");
        AssertNoSecrets(shown);
        Assert.Contains("not JSON", shown);
    }

    [Fact]
    public void Redacting_keeps_numbers_text_and_nulls_as_sent()
    {
        var shown = Redaction.Error("""{"n":12345678901234567890,"f":1.50,"t":"Café <ok>","none":null,"passphrase":null}""");
        Assert.Equal("""{"n":12345678901234567890,"f":1.50,"t":"Café <ok>","none":null,"passphrase":null}""", shown);
    }

    [Fact]
    public async Task An_internal_api_error_body_is_redacted()
    {
        var fake = new FakeUnifi().WithSite();
        fake.Route("GET", "/proxy/network/api/s/default/stat/health", $$"""{"meta":{"rc":"error"},"data":[{"x_passphrase":"{{Passphrase}}"}]}""", 500);
        var settings = Harness.NewSettings(_dir);
        var client = new UnifiClient(settings, new ConsoleTransport(settings, fake));
        var site = await new ConsoleReads(client, new FakeTimeProvider()).SiteAsync(null, default);

        var e = await Assert.ThrowsAsync<UnifiError>(() => site.HealthAsync(default));

        AssertNoSecrets(e.Message);
        Assert.Contains("answered 500", e.Message);
    }

    [Fact]
    public async Task An_integration_api_error_body_read_by_a_composite_tool_is_redacted()
    {
        var fake = new FakeUnifi().Route("GET", "/v1/sites/x/wifi/broadcasts/y", $$"""{"passphrase":"{{Passphrase}}"}""", 400);
        var settings = Harness.NewSettings(_dir);
        var client = new UnifiClient(settings, new ConsoleTransport(settings, fake));

        var e = await Assert.ThrowsAsync<UnifiError>(() => client.GetJsonAsync("/v1/sites/x/wifi/broadcasts/y", default));

        AssertNoSecrets(e.Message);
    }

    /// <summary>
    /// A new document version that adds a credential field fails here, so someone decides whether the name rule already
    /// covers it. Fields the document marks secret are listed with the schema they belong to.
    /// </summary>
    [Fact]
    public void The_secret_fields_of_the_document_are_the_reviewed_ones()
    {
        Assert.Equal(["Hotspot voucher details.code"], Redaction.DocumentedSecrets().Order(StringComparer.Ordinal));

        using var stream = typeof(Redaction).Assembly.GetManifestResourceStream("openapi.json")!;
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array) foreach (var i in e.EnumerateArray()) Walk(i);
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("properties", out var ps) && ps.ValueKind == JsonValueKind.Object)
                foreach (var p in ps.EnumerateObject()) names.Add(p.Name);
            foreach (var p in e.EnumerateObject()) Walk(p.Value);
        }
        Walk(JsonDocument.Parse(stream).RootElement);
        Assert.Equal(["passphrase"], names.Where(Redaction.IsSecretName).Order(StringComparer.Ordinal));

        Assert.True(Redaction.IsSecretName("passphrase"));
        Assert.False(Redaction.IsSecretName("presharedKeyNetworkIds"));
        Assert.False(Redaction.IsSecretName("code"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("voucher-codes", true)]
    [InlineData(" voucher-codes , ", true)]
    public void Voucher_codes_are_revealed_only_when_named(string? value, bool expected) =>
        Assert.Equal(expected, Settings.FromEnvironment(k => k == "UNIFI_REVEAL_SECRETS" ? value : null).RevealVoucherCodes);

    [Theory]
    [InlineData("wifi-passphrases")]
    [InlineData("all")]
    [InlineData("voucher-code")]
    public void An_unknown_secret_to_reveal_stops_the_server(string value) =>
        Assert.Throws<ArgumentException>(() => Settings.FromEnvironment(k => k == "UNIFI_REVEAL_SECRETS" ? value : null));
}
