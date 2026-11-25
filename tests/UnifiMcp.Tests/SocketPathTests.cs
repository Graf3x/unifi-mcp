using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UnifiMcp.Tests;

/// <summary>
/// A fact that makes symlinks. Always runs on Unix; on Windows only where this user may create one (developer mode or
/// an elevated shell), and is skipped otherwise.
/// </summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    private static readonly bool CanLink = Probe();

    public SymlinkFactAttribute()
    {
        if (!CanLink) Skip = "This user may not create symlinks here.";
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return true;
        var dir = Directory.CreateTempSubdirectory("umcp-link").FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine(dir, "target"));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>A fact about how Linux treats Unix sockets, which Windows does not share; skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix sockets behave this way on Unix only.";
    }
}

/// <summary>
/// What the server does with whatever already sits at a socket path when it starts. Only a stale socket is removed;
/// an ordinary file, a link and a live listener are left alone and the start is refused (security review, finding 14).
/// </summary>
public sealed class SocketPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("umcp-sock").FullName;
    private string Tail => Path.Combine(_dir, "t.sock");
    private string Pub => Path.Combine(_dir, "p.sock");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Settings NewSettings() => Harness.NewSettings(_dir) with { Listen = "unix:" + Tail, PublicListen = "unix:" + Pub };

    /// <summary>Builds and starts a server on the test's paths, which must fail (the host claims its sockets as it is built).</summary>
    private Task<Exception> RefusedAsync() => Assert.ThrowsAnyAsync<Exception>(async () =>
    {
        await using var app = McpHost.Build(NewSettings());
        await app.StartAsync();
    });

    private static HttpClient Over(string sock) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(sock), ct);
            return new NetworkStream(s, ownsSocket: true);
        },
    }) { BaseAddress = new Uri("http://localhost") };

    /// <summary>
    /// A socket file with nobody listening on it, as a killed server leaves behind. Bound through a plain endpoint, because
    /// .NET removes the file of a socket it bound to a <see cref="UnixDomainSocketEndPoint"/> when the socket is disposed.
    /// </summary>
    internal static void LeaveStaleSocket(string path)
    {
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Bind(new PlainUnixEndPoint(path));
        s.Listen();
    }

    private sealed class PlainUnixEndPoint(string path) : EndPoint
    {
        private readonly UnixDomainSocketEndPoint _inner = new(path);
        public override AddressFamily AddressFamily => AddressFamily.Unix;
        public override SocketAddress Serialize() => _inner.Serialize();
        public override EndPoint Create(SocketAddress address) => _inner.Create(address);
    }

    // ---- SocketFiles on its own ----------------------------------------------------------------------------------

    [Fact]
    public void Kinds_are_told_apart_without_following_links()
    {
        File.WriteAllText(Path.Combine(_dir, "file"), "x");
        LeaveStaleSocket(Tail);
        Assert.Equal(EntryKind.File, SocketFiles.KindOf(Path.Combine(_dir, "file")));
        Assert.Equal(EntryKind.Other, SocketFiles.KindOf(_dir));
        Assert.Equal(EntryKind.Missing, SocketFiles.KindOf(Path.Combine(_dir, "nothing")));
        Assert.Equal(EntryKind.Missing, SocketFiles.KindOf(Path.Combine(_dir, "nothing", "below")));
        Assert.Equal(EntryKind.Socket, SocketFiles.KindOf(Tail));
    }

    [SymlinkFact]
    public void A_link_is_a_link_whatever_it_points_at()
    {
        File.WriteAllText(Path.Combine(_dir, "file"), "x");
        LeaveStaleSocket(Tail);
        File.CreateSymbolicLink(Path.Combine(_dir, "to-file"), Path.Combine(_dir, "file"));
        File.CreateSymbolicLink(Path.Combine(_dir, "to-socket"), Tail);
        File.CreateSymbolicLink(Path.Combine(_dir, "to-nothing"), Path.Combine(_dir, "nothing"));
        Assert.Equal(EntryKind.Link, SocketFiles.KindOf(Path.Combine(_dir, "to-file")));
        Assert.Equal(EntryKind.Link, SocketFiles.KindOf(Path.Combine(_dir, "to-socket")));
        Assert.Equal(EntryKind.Link, SocketFiles.KindOf(Path.Combine(_dir, "to-nothing")));
    }

    [SymlinkFact]
    public void A_link_to_a_stale_socket_is_left_alone()
    {
        LeaveStaleSocket(Tail);
        var link = Path.Combine(_dir, "link.sock");
        File.CreateSymbolicLink(link, Tail);
        Assert.Throws<IOException>(() => SocketFiles.Claim(link));
        Assert.Equal(EntryKind.Socket, SocketFiles.KindOf(Tail));
        Assert.Equal(EntryKind.Link, SocketFiles.KindOf(link));
    }

    [Fact]
    public void A_claim_holds_the_path_until_it_is_released()
    {
        using (SocketFiles.Claim(Tail))
            Assert.Contains("Another server", Assert.Throws<IOException>(() => SocketFiles.Claim(Tail)).Message);
        SocketFiles.Claim(Tail).Dispose();
    }

    [Fact]
    public void A_lock_path_that_is_not_a_plain_file_is_left_alone()
    {
        Directory.CreateDirectory(Tail + ".lock");
        Assert.Throws<IOException>(() => SocketFiles.Claim(Tail));
        Assert.True(Directory.Exists(Tail + ".lock"));
    }

    [Fact]
    public void A_relative_path_is_refused() =>
        Assert.Throws<IOException>(() => SocketFiles.Claim("run/mcp.sock"));

    [Fact]
    public void A_stale_socket_is_cleared_by_a_claim()
    {
        LeaveStaleSocket(Tail);
        using (SocketFiles.Claim(Tail))
            Assert.Equal(EntryKind.Missing, SocketFiles.KindOf(Tail));
    }

    [UnixFact]
    public async Task A_live_listener_with_a_full_backlog_is_left_alone_without_waiting()
    {
        using var other = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        other.Bind(new UnixDomainSocketEndPoint(Tail));
        other.Listen(1);
        var waiting = new List<Socket>();
        try
        {
            // Connect without accepting until the listener's backlog takes no more.
            var full = false;
            for (var i = 0; i < 64 && !full; i++)
            {
                var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                waiting.Add(client);
                try { client.Connect(new UnixDomainSocketEndPoint(Tail)); }
                catch (SocketException) { full = true; }
            }
            Assert.True(full, "the listener's backlog never filled");

            var claim = Task.Run(() => SocketFiles.Claim(Tail));
            var e = await Assert.ThrowsAsync<IOException>(() => claim.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("Something is listening", e.Message);
            Assert.Equal(EntryKind.Socket, SocketFiles.KindOf(Tail));
        }
        finally
        {
            foreach (var client in waiting) client.Dispose();
        }
    }

    // ---- A public folder this user may not use: the public socket is skipped and the tailnet one still claimed ----

    private SocketClaims ClaimBoth(string pub, out IReadOnlyList<string> bound)
    {
        var claims = new SocketClaims();
        bound = claims.Claim(Harness.NewSettings(_dir) with { Listen = "unix:" + Tail, PublicListen = "unix:" + pub });
        return claims;
    }

    [Fact]
    public void A_public_folder_this_user_may_not_search_skips_the_public_socket()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "public")).FullName;
        var pub = Path.Combine(folder, "p.sock");
        using var denied = FolderDenial.Of(folder, search: true);
        if (!denied.Took) return; // root, or an ACL that does not take: nothing to test on this machine

        using var claims = ClaimBoth(pub, out var bound);

        Assert.Equal([Tail], bound);
        Assert.Contains(pub, claims.PublicSkipped);
    }

    [Fact]
    public void A_public_folder_that_takes_no_new_entries_skips_the_public_socket_though_its_lock_file_exists()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "public")).FullName;
        var pub = Path.Combine(folder, "p.sock");
        File.WriteAllText(pub + ".lock", ""); // left by an earlier run, before the folder's permissions changed
        using var denied = FolderDenial.Of(folder, search: false);
        if (!denied.Took) return;

        using var claims = ClaimBoth(pub, out var bound);

        Assert.Equal([Tail], bound);
        Assert.Contains(pub, claims.PublicSkipped);
        using (new FileStream(pub + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } // not held
    }

    /// <summary>
    /// Takes search (listing) or creating entries in a folder away from this user, until disposed. Unix: the folder's mode;
    /// Windows: a deny rule. <see cref="Took"/> is false when this user can still do it (root, say).
    /// </summary>
    private sealed class FolderDenial : IDisposable
    {
        private readonly string _folder;
        private readonly bool _search;

        private FolderDenial(string folder, bool search)
        {
            _folder = folder;
            _search = search;
        }

        public bool Took { get; private init; }

        public static FolderDenial Of(string folder, bool search)
        {
            if (OperatingSystem.IsWindows()) SetDenied(folder, Rights(search), true);
            else File.SetUnixFileMode(folder, search ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return new FolderDenial(folder, search) { Took = !Can(folder, search) };
        }

        private static bool Can(string folder, bool search)
        {
            try
            {
                if (search) _ = Directory.EnumerateFileSystemEntries(folder).ToList();
                else File.Create(Path.Combine(folder, "probe"), 1, FileOptions.DeleteOnClose).Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        [SupportedOSPlatform("windows")]
        private static FileSystemRights Rights(bool search) => search ? FileSystemRights.ListDirectory : FileSystemRights.CreateFiles;

        [SupportedOSPlatform("windows")]
        private static void SetDenied(string folder, FileSystemRights rights, bool denied)
        {
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
            if (denied) acl.AddAccessRule(rule);
            else acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }

        public void Dispose()
        {
            if (OperatingSystem.IsWindows()) SetDenied(_folder, Rights(_search), false);
            else File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ---- The real server -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_ordinary_file_at_the_socket_path_is_left_alone_and_the_start_refused()
    {
        File.WriteAllText(Tail, "not a socket");
        var e = await RefusedAsync();
        Assert.Contains(Tail, e.ToString());
        Assert.Equal("not a socket", File.ReadAllText(Tail));
    }

    [Fact]
    public async Task An_ordinary_file_at_the_public_path_is_left_alone_too()
    {
        File.WriteAllText(Pub, "not a socket");
        await RefusedAsync();
        Assert.Equal("not a socket", File.ReadAllText(Pub));
    }

    [SymlinkFact]
    public async Task A_symlink_at_the_socket_path_is_left_alone_and_its_target_too()
    {
        var target = Path.Combine(_dir, "target");
        File.WriteAllText(target, "kept");
        File.CreateSymbolicLink(Tail, target);
        await RefusedAsync();
        Assert.Equal(target, new FileInfo(Tail).LinkTarget);
        Assert.Equal("kept", File.ReadAllText(target));
    }

    [SymlinkFact]
    public async Task A_dangling_symlink_at_the_socket_path_is_left_alone()
    {
        File.CreateSymbolicLink(Tail, Path.Combine(_dir, "nowhere"));
        await RefusedAsync();
        Assert.NotNull(new FileInfo(Tail).LinkTarget);
    }

    [Fact]
    public async Task A_second_instance_on_the_same_run_directory_leaves_the_first_one_listening()
    {
        await using var first = McpHost.Build(NewSettings());
        await first.StartAsync();
        await RefusedAsync();
        using var tailnet = Over(Tail);
        Assert.Equal("ok", await tailnet.GetStringAsync("/healthz"));
        using var internet = Over(Pub);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await internet.GetAsync("/healthz")).StatusCode);
        await first.StopAsync();
    }

    [Fact]
    public async Task A_live_listener_of_another_program_is_left_alone()
    {
        using var other = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        other.Bind(new UnixDomainSocketEndPoint(Tail));
        other.Listen();
        await RefusedAsync();
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(Tail)); // still the other program's
    }

    [Fact]
    public async Task A_stale_socket_is_removed_and_the_server_binds_again()
    {
        LeaveStaleSocket(Tail);
        LeaveStaleSocket(Pub);
        Assert.True(File.Exists(Tail));
        await using var app = McpHost.Build(NewSettings());
        await app.StartAsync();
        using var tailnet = Over(Tail);
        Assert.Equal("ok", await tailnet.GetStringAsync("/healthz"));
        using var internet = Over(Pub);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await internet.GetAsync("/healthz")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task The_server_starts_again_on_the_same_paths_after_it_stops()
    {
        await using (var app = McpHost.Build(NewSettings()))
        {
            await app.StartAsync();
            await app.StopAsync();
        }
        await using var again = McpHost.Build(NewSettings());
        await again.StartAsync();
        using var tailnet = Over(Tail);
        Assert.Equal("ok", await tailnet.GetStringAsync("/healthz"));
        await again.StopAsync();
    }

    [Fact]
    public async Task A_refused_start_holds_no_lock()
    {
        File.WriteAllText(Pub, "not a socket");
        await RefusedAsync();
        File.Delete(Pub);
        await using var again = McpHost.Build(NewSettings());
        await again.StartAsync();
        await again.StopAsync();
    }
}
