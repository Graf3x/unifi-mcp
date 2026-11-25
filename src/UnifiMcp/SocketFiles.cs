using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace UnifiMcp;

/// <summary>What is at a path, seen without following a link.</summary>
public enum EntryKind { Missing, File, Socket, Link, Other }

/// <summary>The host's Unix sockets, claimed through <see cref="SocketFiles"/> as it starts and held until it is disposed.</summary>
public sealed class SocketClaims : IDisposable
{
    private readonly List<IDisposable> _locks = [];

    /// <summary>The socket paths claimed, to bind: the tailnet one first.</summary>
    public List<string> Bound { get; } = [];

    /// <summary>Why the public socket is not bound, when it is configured and isn't. The tailnet side starts anyway.</summary>
    public string? PublicSkipped { get; private set; }

    /// <summary>
    /// The folder both bound sockets are in, when they share one. Whatever is given that folder to reach the public socket
    /// (the tunnel container) can then reach the tailnet socket too, whose callers are trusted by the login header alone.
    /// </summary>
    public string? SharedFolder =>
        Bound.Count == 2 && Path.GetDirectoryName(Bound[0]) is { } folder && folder == Path.GetDirectoryName(Bound[1]) ? folder : null;

    /// <summary>
    /// Claims both sockets. The tailnet one must succeed; the public one is skipped when its folder is missing, or this user
    /// may not look in it or create the socket there.
    /// </summary>
    public IReadOnlyList<string> Claim(Settings settings)
    {
        try
        {
            if (Listeners.UnixPath(settings.Listen) is { } tail) Take(tail);
            if (Listeners.UnixPath(settings.PublicListen) is { } pub && !Bound.Contains(pub))
            {
                if (!Directory.Exists(Path.GetDirectoryName(pub)))
                    PublicSkipped = $"the folder of {pub} does not exist";
                else
                {
                    try { Take(pub); }
                    catch (UnauthorizedAccessException) { PublicSkipped = $"this user may not look in or write in the folder of {pub}"; }
                }
            }
            return Bound;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Take(string path)
    {
        _locks.Add(SocketFiles.Claim(path));
        Bound.Add(path);
    }

    public void Dispose()
    {
        foreach (var held in _locks) held.Dispose();
        _locks.Clear();
    }
}

/// <summary>
/// Claims a Unix socket path before the server binds it. A lock file next to the socket says which process owns the path,
/// so a second server on the same run folder stops instead of taking it. Of what is already at the path, only a socket
/// that nobody answers on is removed; a file, a link, a folder or a live listener is left alone and the start is refused.
/// </summary>
public static class SocketFiles
{
    /// <summary>Takes the path's lock and clears a stale socket there. The lock holds until the result is disposed.</summary>
    /// <exception cref="IOException">The path is taken: another server holds its lock, or something other than a stale socket is there.</exception>
    /// <exception cref="UnauthorizedAccessException">This user may not look in the path's folder, or create the socket there.</exception>
    public static IDisposable Claim(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException($"The socket path {path} must be absolute.");
        var lockPath = path + ".lock";
        if (KindOf(lockPath) is not (EntryKind.Missing or EntryKind.File))
            throw new IOException($"{lockPath} is not a plain file, so it was left alone. Remove it, or choose another socket path.");
        FileStream held;
        try { held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when (e is not DirectoryNotFoundException)
        {
            throw new IOException($"Another server holds {lockPath}, so {path} was left alone. Stop it, or give this one another run folder.", e);
        }
        try
        {
            // A lock file left by an earlier run opens in a folder that no longer takes new entries; the bind would fail.
            if (!AcceptsEntries(Path.GetDirectoryName(path)!))
                throw new UnauthorizedAccessException($"This user may not create {path} in its folder.");
            switch (KindOf(path))
            {
                case EntryKind.Missing:
                    break;
                case EntryKind.Socket when !Answers(path):
                    File.Delete(path); // stale: left by a server that did not shut down
                    break;
                case EntryKind.Socket:
                    throw new IOException($"Something is listening on {path}, so it was left alone. Stop it, or choose another socket path.");
                default:
                    throw new IOException($"{path} exists and is not a socket, so it was left alone. Remove it, or choose another socket path.");
            }
        }
        catch
        {
            held.Dispose();
            throw;
        }
        return held;
    }

    /// <summary>The kind of entry at <paramref name="path"/>, without following a link. Missing when nothing is there.</summary>
    /// <exception cref="UnauthorizedAccessException">This user may not look in the folder of <paramref name="path"/>.</exception>
    public static EntryKind KindOf(string path) => OperatingSystem.IsWindows() ? WindowsKindOf(path) : UnixKindOf(path);

    /// <summary>Whether this user may create an entry (the socket) in <paramref name="folder"/>.</summary>
    private static bool AcceptsEntries(string folder)
    {
        if (!OperatingSystem.IsWindows()) return Access(folder, AccessWrite | AccessSearch) == 0;
        try
        {
            File.Create(Path.Combine(folder, $".{Guid.NewGuid():N}.probe"), 1, FileOptions.DeleteOnClose).Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// False only when a connection is refused outright, which is what a socket nobody listens on does. Anything else
    /// (a connection, no permission, a connection left waiting) counts as answering, so the socket is kept.
    /// </summary>
    private static bool Answers(string path)
    {
        // Nonblocking: a live listener whose backlog is full would otherwise keep this start waiting, holding the lock.
        using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
        try
        {
            s.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return false; }
        catch (SocketException) { return true; }
    }

    // ---- Unix: lstat through the runtime's own native shim, whose FileStatus starts { int Flags; int Mode; ... } ----

    private const int SIfMt = 0xF000, SIfSock = 0xC000, SIfLnk = 0xA000, SIfReg = 0x8000, ENoEnt = 2, EAcces = 13;
    private const int AccessSearch = 1, AccessWrite = 2; // the shim's own X_OK and W_OK

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] status);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Access", SetLastError = true)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    private static EntryKind UnixKindOf(string path)
    {
        var status = new byte[512]; // larger than FileStatus on every runtime
        if (LStat(path, status) != 0)
            return Marshal.GetLastPInvokeError() switch
            {
                ENoEnt => EntryKind.Missing,
                EAcces => throw new UnauthorizedAccessException($"This user may not look in the folder of {path}."),
                _ => EntryKind.Other,
            };
        return (BitConverter.ToInt32(status, 4) & SIfMt) switch
        {
            SIfSock => EntryKind.Socket,
            SIfLnk => EntryKind.Link,
            SIfReg => EntryKind.File,
            _ => EntryKind.Other,
        };
    }

    // ---- Windows: a Unix socket is a reparse point with its own tag; FindFirstFile reports the tag without following it ----

    private const uint AttrDirectory = 0x10, AttrReparsePoint = 0x400;
    private const uint TagAfUnix = 0x80000023, TagSymlink = 0xA000000C, TagMountPoint = 0xA0000003;
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string name, byte[] findData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    private static EntryKind WindowsKindOf(string path)
    {
        if (path.IndexOfAny(['*', '?']) >= 0) return EntryKind.Other;
        var data = new byte[1024]; // WIN32_FIND_DATAW is 592 bytes: attributes at 0, the reparse tag (dwReserved0) at 36
        var handle = FindFirstFile(path, data);
        if (handle == new IntPtr(-1))
            return Marshal.GetLastPInvokeError() switch
            {
                ErrorFileNotFound or ErrorPathNotFound => EntryKind.Missing,
                ErrorAccessDenied => throw new UnauthorizedAccessException($"This user may not look in the folder of {path}."),
                _ => EntryKind.Other,
            };
        FindClose(handle);
        var attributes = BitConverter.ToUInt32(data, 0);
        if ((attributes & AttrReparsePoint) != 0)
            return BitConverter.ToUInt32(data, 36) switch
            {
                TagAfUnix => EntryKind.Socket,
                TagSymlink or TagMountPoint => EntryKind.Link,
                _ => EntryKind.Other,
            };
        return (attributes & AttrDirectory) != 0 ? EntryKind.Other : EntryKind.File;
    }
}
