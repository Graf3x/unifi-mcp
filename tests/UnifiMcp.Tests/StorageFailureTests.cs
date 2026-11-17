using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace UnifiMcp.Tests;

/// <summary>
/// The connector's storage in ./data (oauth.db, the keys folder) failing at startup: the public side answers 503 and
/// says why, once; the tailnet socket keeps serving.
/// </summary>
public sealed class StorageFailureTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("unifi-mcp-storage").FullName;
    private string Db => Path.Combine(_dataDir, "oauth.db");
    private string KeysDir => Path.Combine(_dataDir, "keys");

    private static Listener TailnetIfMarked(HttpContext ctx) =>
        ctx.Request.Headers.ContainsKey("X-Test-Tailnet") ? Listener.Tailnet : Listener.Public;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dataDir, recursive: true);
    }

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    /// <summary>Public: 503 everywhere, store endpoints included. Tailnet: tools/list. Logs: the reason in the summary and one Warning, no stack trace.</summary>
    private static async Task AssertOnlyThePublicSideIsDownAsync(PublicHarness h, string reason)
    {
        Assert.Equal(reason, h.App.Services.GetRequiredService<ConnectorState>().Reason);

        using var internet = h.NewClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await internet.PostAsync("/mcp", new StringContent(Initialize, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await internet.PostAsync("/oauth/register", new StringContent("""{"redirect_uris":["https://claude.ai/cb"]}""", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await internet.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token" }))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await internet.GetAsync("/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await internet.GetAsync("/oauth/jwks")).StatusCode);

        var tailnet = h.NewClient();
        tailnet.DefaultRequestHeaders.Add("X-Test-Tailnet", "1");
        tailnet.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Harness.Login);
        await using (var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                     {
                         Endpoint = new Uri(PublicHarness.BaseAddress, "/mcp"),
                         TransportMode = HttpTransportMode.StreamableHttp,
                         Name = "unifi-mcp-tests",
                     }, tailnet, loggerFactory: null, ownsHttpClient: true)))
            Assert.NotEmpty(await client.ListToolsAsync());

        var summary = Assert.Single(h.Logs.Lines, l => l.StartsWith("unifi-mcp on ", StringComparison.Ordinal));
        Assert.Contains($"connector unavailable ({reason})", summary);
        var warning = Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Line.Contains(reason, StringComparison.Ordinal));
        Assert.Equal($"Connector unavailable: {reason}.", warning.Line);
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains("SqliteException (", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("garbage")]
    public async Task An_oauth_db_that_cannot_be_opened_takes_down_only_the_public_side(string kind)
    {
        if (kind == "directory") Directory.CreateDirectory(Db);
        else File.WriteAllBytes(Db, Enumerable.Repeat((byte)0x5A, 8192).ToArray());

        await using var h = await PublicHarness.StartAsync(dataDir: _dataDir, listenerOf: TailnetIfMarked);

        await AssertOnlyThePublicSideIsDownAsync(h, $"cannot open {Db} (SqliteException)");
    }

    [Fact]
    public async Task A_keys_path_that_is_a_file_takes_down_only_the_public_side()
    {
        File.WriteAllText(KeysDir, "not a folder");

        await using var h = await PublicHarness.StartAsync(dataDir: _dataDir, listenerOf: TailnetIfMarked);

        await AssertOnlyThePublicSideIsDownAsync(h, $"cannot read {KeysDir} (not a directory)");
    }

    [Fact]
    public async Task A_keys_folder_that_cannot_be_listed_takes_down_only_the_public_side()
    {
        Directory.CreateDirectory(KeysDir);
        var exceptionType = DenyListing(KeysDir);
        try
        {
            // Root (or an ACL that does not take) can still list it: nothing to test on this machine.
            if (exceptionType is null) return;

            await using var h = await PublicHarness.StartAsync(dataDir: _dataDir, listenerOf: TailnetIfMarked);

            await AssertOnlyThePublicSideIsDownAsync(h, $"cannot read {KeysDir} ({exceptionType})");
        }
        finally
        {
            AllowListing(KeysDir);
        }
    }

    /// <summary>Makes the folder unlistable for this user; the exception type listing it now throws, or null when it still lists.</summary>
    private static string? DenyListing(string dir)
    {
        if (OperatingSystem.IsWindows()) SetListingDenied(dir, true);
        else File.SetUnixFileMode(dir, UnixFileMode.None);
        try
        {
            _ = Directory.EnumerateFiles(dir, "key-*.xml").ToList();
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name;
        }
    }

    private static void AllowListing(string dir)
    {
        if (OperatingSystem.IsWindows()) SetListingDenied(dir, false);
        else File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void SetListingDenied(string dir, bool denied)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        if (denied) acl.AddAccessRule(rule);
        else acl.RemoveAccessRule(rule);
        info.SetAccessControl(acl);
    }
}
