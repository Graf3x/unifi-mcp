using Microsoft.Extensions.Logging;

namespace UnifiMcp.Tests;

/// <summary>
/// The tunnel container reaches the public socket and nothing else: the public socket has a folder of its own, and the
/// Compose files mount only that folder into cloudflared (security review, finding 4). tests/compose/socket-isolation.sh
/// checks the same on a running Linux host.
/// </summary>
public sealed class SocketIsolationTests : IDisposable
{
    private const string PublicMount = "./run-public:/run/unifi-mcp-public";
    private const string TailnetMount = "./run:/run/unifi-mcp";
    private readonly string _dir = Directory.CreateTempSubdirectory("umcp-iso").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Folder(string unixPath) => unixPath[..unixPath.LastIndexOf('/')];

    [Fact]
    public void The_public_socket_has_a_folder_of_its_own_by_default()
    {
        var s = new Settings();
        Assert.Equal("/run/unifi-mcp/mcp.sock", Listeners.UnixPath(s.Listen));
        Assert.Equal("/run/unifi-mcp-public/public.sock", Listeners.UnixPath(s.PublicListen));
        Assert.NotEqual(Folder(Listeners.UnixPath(s.Listen)!), Folder(Listeners.UnixPath(s.PublicListen)!));
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("deploy/compose.yaml")]
    public void The_tunnel_container_mounts_only_the_public_folder(string file)
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.Equal([PublicMount], Volumes(compose, "cloudflared"));
        var server = Volumes(compose, "unifi-mcp");
        Assert.Contains(TailnetMount, server);
        Assert.Contains(PublicMount, server);
        // The mount is where the server puts the public socket, and nowhere near the tailnet one.
        Assert.Equal(Folder(Listeners.UnixPath(new Settings().PublicListen)!), PublicMount.Split(':')[1]);
        Assert.DoesNotContain("MCP_PUBLIC_LISTEN", compose);
    }

    [Fact]
    public void The_setup_guides_route_the_tunnel_to_the_public_folder()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("`unix:/run/unifi-mcp-public/public.sock`", readme);
        Assert.Contains("mkdir -p data run run-public", readme);
    }

    [Fact]
    public async Task Sockets_that_share_a_folder_are_warned_about()
    {
        var warnings = await StartAndWarnAsync(Path.Combine(_dir, "t.sock"), Path.Combine(_dir, "p.sock"));
        Assert.Contains(warnings, w => w.Contains("share the folder") && w.Contains(_dir));
    }

    [Fact]
    public async Task Sockets_in_folders_of_their_own_are_not()
    {
        var pub = Directory.CreateDirectory(Path.Combine(_dir, "public")).FullName;
        var warnings = await StartAndWarnAsync(Path.Combine(_dir, "t.sock"), Path.Combine(pub, "p.sock"));
        Assert.DoesNotContain(warnings, w => w.Contains("share the folder"));
        Assert.DoesNotContain(warnings, w => w.Contains("Public socket not bound"));
    }

    [Fact]
    public async Task A_missing_public_folder_is_a_warning_when_the_connector_is_meant_to_run()
    {
        var missing = Path.Combine(_dir, "no-such-folder", "p.sock");
        var warnings = await StartAndWarnAsync(Path.Combine(_dir, "t.sock"), missing, new Uri("https://unifi.example.com"));
        Assert.Contains(warnings, w => w.Contains("Public socket not bound") && w.Contains(missing));
    }

    [Fact]
    public async Task A_missing_public_folder_is_no_warning_without_a_public_url()
    {
        // Guide 4 (the LAN compose file) mounts no public folder and sets no public URL.
        var warnings = await StartAndWarnAsync(Path.Combine(_dir, "t.sock"), Path.Combine(_dir, "no-such-folder", "p.sock"));
        Assert.DoesNotContain(warnings, w => w.Contains("Public socket not bound"));
    }

    private async Task<IReadOnlyList<string>> StartAndWarnAsync(string tail, string pub, Uri? publicBase = null)
    {
        var logs = new LogCapture();
        var settings = Harness.NewSettings(_dir) with { Listen = "unix:" + tail, PublicListen = "unix:" + pub, PublicBaseUrl = publicBase };
        await using var app = McpHost.Build(settings, new FakeUnifi(), b => b.Logging.AddProvider(logs));
        await app.StartAsync();
        await app.StopAsync();
        return [.. logs.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Line)];
    }

    /// <summary>The list items under one service's <c>volumes:</c>, in a Compose file indented the way this repo's are.</summary>
    private static List<string> Volumes(string compose, string service)
    {
        var lines = compose.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.IndexOf($"  {service}:");
        Assert.True(start >= 0, $"no {service} service");
        var volumes = new List<string>();
        var inVolumes = false;
        foreach (var line in lines.Skip(start + 1))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            if (!line.StartsWith("   ", StringComparison.Ordinal)) break; // the next service, or a top-level key
            if (!line.StartsWith("     ", StringComparison.Ordinal)) inVolumes = text == "volumes:";
            else if (inVolumes && text.StartsWith("- ", StringComparison.Ordinal)) volumes.Add(text[2..].Trim());
        }
        return volumes;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "unifi-mcp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
