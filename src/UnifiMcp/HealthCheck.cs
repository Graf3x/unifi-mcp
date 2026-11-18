using System.Net.Sockets;

namespace UnifiMcp;

/// <summary>
/// <c>unifi-mcp --healthcheck</c>: GETs /healthz on the tailnet listener (mcp.sock, or its TCP address in development) and
/// exits 0 when the server answers, 1 otherwise. It is for Docker's HEALTHCHECK, since the runtime image has no curl and
/// the sockets are reachable only from inside the container. It checks that the server is up, not that it is configured:
/// a server still waiting for its API key is healthy, so its setup page and tunnel stay reachable.
/// </summary>
public static class HealthCheck
{
    public const string Argument = "--healthcheck";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static async Task<int> RunAsync(Func<string, string?> env, TextWriter output)
    {
        try
        {
            var settings = Settings.FromEnvironment(env);
            var (handler, url) = Target(settings.Listen);
            using var http = new HttpClient(handler) { Timeout = Timeout };
            using var r = await http.GetAsync(url);
            var body = await r.Content.ReadAsStringAsync();
            if (r.IsSuccessStatusCode && body.Trim() == "ok")
            {
                output.WriteLine("ok");
                return 0;
            }
            output.WriteLine($"unhealthy: /healthz answered {(int)r.StatusCode}");
            return 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"unhealthy: {e.Message}");
            return 1;
        }
    }

    /// <summary>How to reach the tailnet listener from inside: its socket, or its first TCP URL with a wildcard host made loopback.</summary>
    internal static (HttpMessageHandler Handler, Uri Url) Target(string listen)
    {
        if (Listeners.UnixPath(listen) is { } path)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await s.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                        return new NetworkStream(s, ownsSocket: true);
                    }
                    catch
                    {
                        s.Dispose();
                        throw;
                    }
                },
            };
            return (handler, new Uri("http://localhost/healthz"));
        }
        var first = listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? throw new ArgumentException("MCP_LISTEN is empty.");
        var local = first.Replace("://*", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://[::]", "://[::1]", StringComparison.Ordinal);
        return (new SocketsHttpHandler(), new Uri(new Uri(local), "/healthz"));
    }
}
