using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace UnifiMcp;

/// <summary>Which of the server's listeners a request arrived on. Decided by the socket, never by anything the caller sends.</summary>
public enum Listener { Tailnet, Public, Setup, Lan, Unknown }

public static class Listeners
{
    private sealed record Feature(Listener Listener);

    /// <summary>Maps the local end of a connection to a listener. Anything unrecognised, including null, is Unknown.</summary>
    public static Listener Of(EndPoint? local, Settings s)
    {
        switch (local)
        {
            case UnixDomainSocketEndPoint unix:
                var path = unix.ToString();
                if (UnixPath(s.PublicListen) is { } pub && string.Equals(path, pub, StringComparison.Ordinal)) return Listener.Public;
                if (UnixPath(s.Listen) is { } tail && string.Equals(path, tail, StringComparison.Ordinal)) return Listener.Tailnet;
                return Listener.Unknown;
            case IPEndPoint ip:
                if (s.SetupPage && SetupEndpoint(s.SetupListen)?.Port == ip.Port) return Listener.Setup;
                if (s.LanListen is { } lan && SetupEndpoint(lan)?.Port == ip.Port) return Listener.Lan;
                if (PortOf(s.PublicListen) == ip.Port) return Listener.Public;
                if (PortOf(s.Listen) == ip.Port) return Listener.Tailnet;
                return Listener.Unknown;
            default:
                return Listener.Unknown;
        }
    }

    public static string? UnixPath(string listen) =>
        listen.StartsWith("unix:", StringComparison.Ordinal) ? listen["unix:".Length..] : null;

    /// <summary>The ports of every URL in a TCP listen value (they may be separated by semicolons).</summary>
    public static IEnumerable<int> PortsOf(string listen) =>
        UnixPath(listen) is not null ? [] :
        listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal))
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : 0)
            .Where(p => p > 0);

    public static int? PortOf(string listen)
    {
        if (UnixPath(listen) is not null) return null;
        var first = listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null) return null;
        first = first.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal);
        return Uri.TryCreate(first, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : null;
    }

    /// <summary>
    /// MCP_SETUP_LISTEN (and MCP_LAN_LISTEN) parsed: http, an IP address (0.0.0.0 and [::] for every interface) or localhost, and an explicit port.
    /// Null for anything else, so a host name can never widen where the page answers.
    /// </summary>
    public static (IPAddress? Address, int Port)? SetupEndpoint(string listen)
    {
        if (!Uri.TryCreate(listen, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.IsDefaultPort
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0 || uri.Port is < 1 or > 65535)
            return null;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return (null, uri.Port);
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) ? (ip, uri.Port) : null;
    }

    /// <summary>The first middleware: records the listener on the request so later steps cannot disagree about it.</summary>
    public static void Classify(HttpContext ctx, Settings s, Func<HttpContext, Listener>? listenerOf, UnixConnections unix)
    {
        var listener = listenerOf is not null
            ? listenerOf(ctx)
            : Of(ctx.Connection.LocalIpAddress is { } ip ? new IPEndPoint(ip, ctx.Connection.LocalPort) : unix.Find(ctx.Connection.Id), s);
        ctx.Features.Set(new Feature(listener));
    }

    public static Listener ListenerOf(this HttpContext ctx) => ctx.Features.Get<Feature>()?.Listener ?? Listener.Unknown;
}

/// <summary>
/// Kestrel does not expose a Unix socket's own path to the request, so each Unix listener stamps its connections
/// here (keyed by connection id) and the request looks its connection up. The path is the one the socket was bound to.
/// </summary>
public sealed class UnixConnections
{
    private readonly ConcurrentDictionary<string, EndPoint> _byConnection = new();

    public EndPoint? Find(string connectionId) => _byConnection.GetValueOrDefault(connectionId);

    public void Listen(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions k, string path) =>
        k.ListenUnixSocket(path, o => o.Use(next => async connection =>
        {
            _byConnection[connection.ConnectionId] = new UnixDomainSocketEndPoint(path);
            try { await next(connection); }
            finally { _byConnection.TryRemove(connection.ConnectionId, out _); }
        }));
}