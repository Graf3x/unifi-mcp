using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>One MCP tool generated from one API operation. Writes are only created when the flag is on and need the
/// owner's approval (<see cref="WriteApproval"/>).</summary>
public sealed class ApiTool(ApiOperation op, UnifiClient client) : McpServerTool
{
    public override Tool ProtocolTool { get; } = new()
    {
        Name = op.ToolName,
        Title = op.Summary,
        Description = op.ToolDescription(),
        InputSchema = JsonSerializer.SerializeToElement(op.InputSchema()),
        Annotations = new ToolAnnotations
        {
            Title = op.Summary,
            ReadOnlyHint = !op.IsWrite,
            DestructiveHint = op.IsWrite,
            IdempotentHint = op.Method == HttpMethod.Get || op.Method == HttpMethod.Put || op.Method == HttpMethod.Delete,
            OpenWorldHint = false,
        },
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <summary>The arguments the schema names; it allows no others.</summary>
    private readonly string[] _known = [.. op.InputSchema()["properties"]!.AsObject().Select(p => p.Key)];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        // Refused before anything else: an argument the request never uses would still be shown on the owner's form.
        var unknown = args.Keys.Where(k => !_known.Contains(k, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            return new(ToolText.Error($"{op.ToolName} has no parameter {string.Join(", ", unknown.Select(u => $"'{u}'"))}. "
                                      + $"It takes: {string.Join(", ", _known)}."));
        return new(RunAsync(op, client, args, request, cancellationToken));
    }

    /// <summary>One operation with its arguments: the write's approval, path and query, the call, the audit line.</summary>
    internal static async Task<CallToolResult> RunAsync(ApiOperation op, UnifiClient client, IDictionary<string, JsonElement> args,
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var body = args.TryGetValue("body", out var b) ? b.GetRawText() : null;
        string? approval = null;
        string? path = null;
        try
        {
            // The request is built and checked first, so the owner approves the path that will be sent, and never a call
            // that would then be refused for a missing argument.
            if (op.HasBody && body is null) throw new UnifiError("body is required.");
            path = await BuildPathAsync(op, client, args, cancellationToken);
            if (op.IsWrite)
                switch (WriteApproval.Decide(op, path, args, request))
                {
                    case WriteApproval.Stop stop: return stop.Result;
                    case WriteApproval.Run run: approval = run.How; break;
                }
            var response = await client.SendAsync(op.Method, path, op.HasBody ? body : null, cancellationToken);
            Log(op, request, path, response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture), approval);
            if (!response.Ok) return ToolText.Error($"The console answered {response.Status} to {op.Method} {path.Split('?')[0]}: {response.Body}");
            return ToolText.Ok(response.Body.Length == 0 ? $"Done ({response.Status}, no content)." : response.Body);
        }
        // An approved write that got no answer, or whose caller gave up waiting, may still have reached the console, so it
        // keeps its approval on record.
        catch (Exception e) when (e is UnifiError or UnifiNotConfigured)
        {
            if (approval is not null) Log(op, request, path!, "no answer", approval);
            return ToolText.Error(e.Message);
        }
        catch (OperationCanceledException) when (approval is not null)
        {
            Log(op, request, path!, "cancelled", approval);
            throw;
        }
    }

    private static async Task<string> BuildPathAsync(ApiOperation op, UnifiClient client, IDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var path = op.Path;
        var query = new List<string>();
        foreach (var p in op.Params)
        {
            var value = Value(args, p.Name);
            if (p.In == "path")
            {
                if (p.Name == "siteId") value = (await client.FindSiteAsync(value, op.IsWrite, ct)).Id;
                if (string.IsNullOrWhiteSpace(value)) throw new UnifiError($"{p.Name} is required.");
                // EscapeDataString leaves dots alone, and "." or ".." would change which resource the path names.
                if (value is "." or "..") throw new UnifiError($"{p.Name} is not a valid value.");
                path = path.Replace("{" + p.Name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                query.Add($"{p.Name}={Uri.EscapeDataString(value)}");
            }
            else if (p.Required)
            {
                throw new UnifiError($"{p.Name} is required.");
            }
        }
        return query.Count == 0 ? path : path + "?" + string.Join('&', query);
    }

    private static string? Value(IDictionary<string, JsonElement> args, string name) =>
        !args.TryGetValue(name, out var v) ? null
        : v.ValueKind == JsonValueKind.String ? v.GetString()
        : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText()
        : null;

    /// <summary>What was sent to the console: method, path and status only, never the body or arguments, and for a write
    /// how it was approved. <see cref="Audit.Calls"/> adds the call's outcome.</summary>
    private static void Log(ApiOperation op, RequestContext<CallToolRequestParams> request, string path, string status, string? approval)
    {
        var logger = Audit.Logger(request.Services);
        if (approval is null)
            logger?.Log(Audit.Level(op.IsWrite), "{Login} {Tool} {Method} {Path} -> {Status}",
                Audit.Login(request.User), op.ToolName, op.Method, path.Split('?')[0], status);
        else
            logger?.Log(Audit.Level(op.IsWrite), "{Login} {Tool} {Method} {Path} -> {Status} ({Approval})",
                Audit.Login(request.User), op.ToolName, op.Method, path.Split('?')[0], status, approval);
    }
}

/// <summary>
/// The audit log (category <see cref="Category"/>): who ran what, writes at Information and reads at Debug. It holds the
/// login, tool, method, path, status and outcome only, never a body or arguments, because those can carry secrets.
/// </summary>
public static class Audit
{
    public const string Category = "UnifiMcp.Audit";

    /// <summary>Who called: the name claim, or the subject for a code sign-in's token, which has no email.</summary>
    public static string Login(ClaimsPrincipal? user) =>
        user?.Identity?.Name is { Length: > 0 } name ? name : user?.FindFirst("sub")?.Value ?? "?";

    public static ILogger? Logger(IServiceProvider? services) => services?.GetService<ILoggerFactory>()?.CreateLogger(Category);

    public static LogLevel Level(bool write) => write ? LogLevel.Information : LogLevel.Debug;

    /// <summary>
    /// The call-tool filter: one line per call of any tool, "{Login} {Tool} -> ok", "error" (isError), "asked" (it stopped to
    /// ask the owner, see <see cref="WriteApproval"/>) or "failed" (it threw). A tool not marked read-only counts as a write.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Calls(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, ct) =>
        {
            var tool = (request.MatchedPrimitive as McpServerTool)?.ProtocolTool;
            var outcome = "failed";
            try
            {
                var result = await next(request, ct);
                outcome = result.IsError == true ? "error" : "ok";
                return result;
            }
            catch (InputRequiredException)
            {
                outcome = "asked";
                throw;
            }
            finally
            {
                Logger(request.Services)?.Log(Level(tool?.Annotations?.ReadOnlyHint != true), "{Login} {Tool} -> {Outcome}",
                    Login(request.User), tool?.Name ?? request.Params?.Name ?? "?", outcome);
            }
        };
}

public static class ToolText
{
    /// <summary>Below Claude Code's 50,000-character point where a tool result is written to a file instead, and about 10-15k tokens.</summary>
    public const int Limit = 40_000;

    public static CallToolResult Ok(string text) => Result(text, false);
    public static CallToolResult Error(string text) => Result(text, true);

    private static CallToolResult Result(string text, bool isError) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = Cap(text) }],
    };

    internal static string Cap(string text) => text.Length <= Limit
        ? text
        : text[..Limit] + "\n\n[truncated: the answer was too long. Narrow it: filter, limit and offset on list tools, or a network, client, device or shorter window on the others.]";
}
