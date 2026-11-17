using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace UnifiMcp.Tests;

public sealed class DataKeyTests : IDisposable
{
    public const string KeyA = "0123456789abcdef0123456789abcdef";
    public const string KeyB = "fedcba9876543210fedcba9876543210";

    private readonly string _dir = Directory.CreateTempSubdirectory("unifi-mcp-keys").FullName;
    private string DataDir => Path.Combine(_dir, "data");
    private string KeysDir => Path.Combine(DataDir, "keys");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>DataProtection exactly as the host wires it, without the rest of the host.</summary>
    public static ServiceProvider Protection(string dataDir, string dataKey, ConnectorState? state = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        DataKeys.AddProtectedDataProtection(services, new Settings { DataDir = dataDir, DataKey = dataKey }, state ?? new ConnectorState());
        return services.BuildServiceProvider();
    }

    private void ProtectOnce(string dataKey)
    {
        using var sp = Protection(DataDir, dataKey);
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("hello");
    }

    private Dictionary<string, byte[]> Snapshot() =>
        Directory.GetFiles(KeysDir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    [Fact]
    public void Derive_is_hkdf_sha256_with_the_pinned_info()
    {
        var key = DataKeys.Derive(KeyA);
        Assert.Equal(32, key.Length);
        Assert.Equal(key, DataKeys.Derive(KeyA));
        Assert.NotEqual(key, DataKeys.Derive(KeyB));
        var expected = System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Text.Encoding.UTF8.GetBytes(KeyA), 32, [], System.Text.Encoding.UTF8.GetBytes("unifi-mcp keyring v1"));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Keyring_files_hold_no_plain_key_material()
    {
        ProtectOnce(KeyA);

        var files = Directory.GetFiles(KeysDir, "key-*.xml");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var xml = File.ReadAllText(file);
            Assert.Contains("<aesGcm", xml);
            Assert.DoesNotContain("<masterKey", xml);
            Assert.DoesNotContain("<value>", xml);
        }
    }

    [Fact]
    public void Protected_data_round_trips_through_a_new_keyring_instance()
    {
        string protectedText;
        using (var sp = Protection(DataDir, KeyA))
            protectedText = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("hello");

        using var again = Protection(DataDir, KeyA);
        Assert.Equal("hello", again.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
    }

    [Fact]
    public void Check_on_a_missing_or_empty_keys_directory_is_ok()
    {
        Assert.Equal((true, null), DataKeys.Check(KeysDir, DataKeys.Derive(KeyA)));
        Directory.CreateDirectory(KeysDir);
        Assert.Equal((true, null), DataKeys.Check(KeysDir, DataKeys.Derive(KeyA)));
    }

    [Fact]
    public void Wrong_data_key_is_detected_without_rewriting()
    {
        ProtectOnce(KeyA);
        var before = Snapshot();

        Assert.Equal((true, null), DataKeys.Check(KeysDir, DataKeys.Derive(KeyA)));
        var (ok, reason) = DataKeys.Check(KeysDir, DataKeys.Derive(KeyB));
        Assert.False(ok);
        Assert.Equal($"UNIFI_MCP_DATA_KEY does not match {KeysDir}", reason);

        // The wired-up keyring with the wrong key marks the connector broken and never generates or rewrites a key.
        var state = new ConnectorState();
        using (var sp = Protection(DataDir, KeyB, state))
        {
            Assert.False(state.Ready);
            Assert.Equal(reason, state.Reason);
            var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            Assert.ThrowsAny<Exception>(() => protector.Protect("hello"));
        }

        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, bytes) in before) Assert.Equal(bytes, after[name]);
    }

    [Fact]
    public async Task Wrong_data_key_is_reported_in_the_startup_summary()
    {
        ProtectOnce(KeyA);

        await using var h = await PublicHarness.StartAsync(dataDir: DataDir, dataKey: KeyB);

        var summary = Assert.Single(h.Logs.Lines, l => l.StartsWith("unifi-mcp on ", StringComparison.Ordinal));
        Assert.Contains($"UNIFI_MCP_DATA_KEY does not match {KeysDir}", summary);
        Assert.DoesNotContain("ready", summary);
        Assert.DoesNotContain(KeyA, summary);
        Assert.DoesNotContain(KeyB, summary);
    }

    [Fact]
    public async Task A_matching_data_key_reports_the_connector_ready()
    {
        ProtectOnce(KeyA);

        await using var h = await PublicHarness.StartAsync(dataDir: DataDir, dataKey: KeyA);

        var summary = Assert.Single(h.Logs.Lines, l => l.StartsWith("unifi-mcp on ", StringComparison.Ordinal));
        Assert.Contains("connector ready", summary);
        Assert.DoesNotContain(KeyA, summary);
    }

    [Fact]
    public async Task Wrong_data_key_gives_503_on_public_and_tailnet_still_serves()
    {
        ProtectOnce(KeyA);
        var before = Snapshot();

        // One app, both sides: a request marked X-Test-Tailnet arrives on the tailnet listener, the rest on the public one.
        await using var h = await PublicHarness.StartAsync(dataDir: DataDir, dataKey: KeyB,
            listenerOf: ctx => ctx.Request.Headers.ContainsKey("X-Test-Tailnet") ? Listener.Tailnet : Listener.Public);

        var pub = await h.Http.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pub.StatusCode);
        var body = await pub.Content.ReadAsStringAsync();
        Assert.DoesNotContain(KeyA, body);
        Assert.DoesNotContain(KeyB, body);
        Assert.Equal($"UNIFI_MCP_DATA_KEY does not match {KeysDir}", h.App.Services.GetRequiredService<ConnectorState>().Reason);

        var tailnet = h.App.GetTestClient();
        tailnet.DefaultRequestHeaders.Add("X-Test-Tailnet", "1");
        tailnet.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Harness.Login);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(tailnet.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "unifi-mcp-tests",
        }, tailnet, loggerFactory: null, ownsHttpClient: false));
        Assert.NotEmpty(await client.ListToolsAsync());

        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, bytes) in before) Assert.Equal(bytes, after[name]);
    }
}
