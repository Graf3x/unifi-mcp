using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UnifiMcp.Tests;

/// <summary>Stands in for the UDM. Routes are relative to the integration API root; anything else answers 404.</summary>
public sealed class FakeUnifi : HttpMessageHandler
{
    public const string Prefix = "/proxy/network/integration";
    public sealed record Seen(string Method, string Path, string? Body, string? ApiKey, string Scheme, string Host);

    private readonly Dictionary<string, (int Status, string Json)> _routes = new(StringComparer.Ordinal);
    public List<Seen> Requests { get; } = [];
    public Exception? Throw { get; set; }
    /// <summary>Only requests with this method throw <see cref="Throw"/>; any method when null.</summary>
    public string? ThrowOn { get; set; }

    public FakeUnifi Route(string method, string path, string json, int status = 200)
    {
        _routes[$"{method} {path}"] = (status, json);
        return this;
    }

    public FakeUnifi WithSite() => Route("GET", "/v1/sites", Page($$"""{"id":"{{Harness.SiteId}}","internalReference":"default","name":"Default"}"""));

    public static string Page(params string[] items) =>
        $$"""{"offset":0,"limit":200,"count":{{items.Length}},"totalCount":{{items.Length}},"data":[{{string.Join(",", items)}}]}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
        var key = req.Headers.TryGetValues("X-API-Key", out var v) ? v.Single() : null;
        var uri = req.RequestUri!;
        var full = uri.PathAndQuery.StartsWith(Prefix, StringComparison.Ordinal) ? uri.PathAndQuery[Prefix.Length..] : uri.PathAndQuery;
        Requests.Add(new(req.Method.Method, full, body, key, uri.Scheme, uri.Host));
        if (Throw is not null && (ThrowOn is null || ThrowOn == req.Method.Method)) throw Throw;
        var path = full.Split('?')[0];
        var hit = _routes.TryGetValue($"{req.Method.Method} {full}", out var r) || _routes.TryGetValue($"{req.Method.Method} {path}", out r);
        return hit
            ? new HttpResponseMessage((HttpStatusCode)r.Status) { Content = new StringContent(r.Json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"statusCode":404,"message":"no route"}""", Encoding.UTF8, "application/json") };
    }
}

/// <summary>The real host on a TestServer, driven by the official MCP client the way Claude would.</summary>
public sealed class Harness : IAsyncDisposable
{
    public const string ApiKey = "TEST-KEY-do-not-leak-0123456789";
    public const string Login = "owner@example.test";
    public const string SiteId = "11111111-1111-1111-1111-111111111111";
    public const string Pin = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    public FakeUnifi Unifi { get; } = new();
    public WebApplication App { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public string Dir { get; } = Directory.CreateTempSubdirectory("unifi-mcp-test").FullName;
    /// <summary>Every log line the host writes, formatted; the audit category is captured down to Debug.</summary>
    public LogCapture Logs { get; } = new();

    private static readonly OpenApiCatalog Catalog = OpenApiCatalog.Load();

    /// <summary>The startup line for these settings, as the host would log it with this many tools.</summary>
    public static string Summary(Settings settings, int tools) => McpHost.StartupSummary(settings, EnabledWrites.From(settings, Catalog), tools);

    public static Settings NewSettings(string dir, bool writes = false, bool key = true, bool pin = true, bool confirmFallback = false,
        IReadOnlyList<string>? writeTools = null)
    {
        if (key) File.WriteAllText(Path.Combine(dir, "key"), ApiKey + "\n");
        if (pin) File.WriteAllText(Path.Combine(dir, "pin"), Pin);
        File.WriteAllText(Path.Combine(dir, "logins"), Login + "\n");
        return new Settings
        {
            ApiKeyFile = Path.Combine(dir, "key"),
            CertPinFile = Path.Combine(dir, "pin"),
            AllowedLoginsFile = Path.Combine(dir, "logins"),
            AllowWrites = writes,
            AllowConfirmFallback = confirmFallback,
            WriteTools = writeTools,
            Listen = "http://127.0.0.1:0",
        };
    }

    /// <param name="configure">Runs on the builder after the harness's own setup (e.g. a fake TimeProvider).</param>
    /// <param name="confirmFallback">UNIFI_ALLOW_CONFIRM_FALLBACK: a client that cannot show a form may approve with confirm.</param>
    /// <param name="writeTools">UNIFI_WRITE_TOOLS: the only write tools to register; null registers them all.</param>
    public static async Task<Harness> StartAsync(bool writes = false, bool key = true, bool pin = true, string? login = Login,
        Func<HttpContext, Listener>? listenerOf = null, Action<WebApplicationBuilder>? configure = null, bool confirmFallback = false,
        IReadOnlyList<string>? writeTools = null)
    {
        var h = new Harness();
        h.App = McpHost.Build(NewSettings(h.Dir, writes, key, pin, confirmFallback, writeTools), h.Unifi, b =>
        {
            b.WebHost.UseTestServer();
            b.Logging.AddProvider(h.Logs);
            b.Logging.AddFilter("UnifiMcp.Audit", LogLevel.Debug);
            configure?.Invoke(b);
        }, listenerOf ?? (_ => Listener.Tailnet));
        await h.App.StartAsync();
        h.Http = h.App.GetTestClient();
        if (login is not null) h.Http.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, login);
        if (login == Login) h.Client = await h.ConnectAsync();
        return h;
    }

    /// <param name="elicit">Answers a form the server asks for, with "accept", "decline" or "cancel"; without it the client
    /// declares no forms.</param>
    /// <param name="protocolVersion">Pins a revision, e.g. "2025-11-25"; by default the client negotiates the newest.</param>
    /// <param name="through">Wraps the test server's handler, to see or change what the client sends.</param>
    /// <param name="capabilities">What the client declares, beyond what its handlers imply.</param>
    public Task<McpClient> ConnectAsync(Func<ElicitRequestParams, string>? elicit = null, string? protocolVersion = null,
        Func<HttpMessageHandler, HttpMessageHandler>? through = null, ClientCapabilities? capabilities = null)
    {
        var options = new McpClientOptions { ProtocolVersion = protocolVersion, Capabilities = capabilities };
        if (elicit is not null)
            options.Handlers.ElicitationHandler = (p, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = elicit(p!) });
        var http = Http;
        if (through is not null)
        {
            http = new HttpClient(through(App.GetTestServer().CreateHandler())) { BaseAddress = Http.BaseAddress };
            http.DefaultRequestHeaders.Add(CallerPolicy.LoginHeader, Login);
        }
        return McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "unifi-mcp-tests",
        }, http, loggerFactory: null, ownsHttpClient: through is not null), options);
    }

    public async Task<(string Text, bool IsError)> CallAsync(string tool, Dictionary<string, object?>? args = null)
    {
        var r = await Client.CallToolAsync(tool, args ?? []);
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    /// <summary>The whole result, for tools that also answer with structured content.</summary>
    public async Task<CallToolResult> CallRawAsync(string tool, Dictionary<string, object?>? args = null) =>
        await Client.CallToolAsync(tool, args ?? []);

    public async ValueTask DisposeAsync()
    {
        if (Client is not null) await Client.DisposeAsync();
        await App.StopAsync();
        await App.DisposeAsync();
        Directory.Delete(Dir, recursive: true);
    }
}
