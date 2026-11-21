using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>One MCP tool generated from one API operation. Writes are only created when the flag is on and need a confirm.</summary>
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

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        new(RunAsync(op, client, request.Params?.Arguments ?? new Dictionary<string, JsonElement>(), request, cancellationToken));

    /// <summary>One operation with its arguments: the write confirm, path and query, the call, the audit line.</summary>
    internal static async Task<CallToolResult> RunAsync(ApiOperation op, UnifiClient client, IDictionary<string, JsonElement> args,
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var body = args.TryGetValue("body", out var b) ? b.GetRawText() : null;
        if (op.IsWrite && Value(args, "confirm") != op.ToolName)
            return ToolText.Ok($"Nothing was sent. {op.Method} {op.Path} changes the live network.\n"
                               + $"Arguments: {JsonSerializer.Serialize(args.Where(a => a.Key != "confirm").ToDictionary())}\n"
                               + $"To run it, call {op.ToolName} again with confirm set to exactly \"{op.ToolName}\".");
        try
        {
            if (op.HasBody && body is null) throw new UnifiError("body is required.");
            var path = await BuildPathAsync(op, client, args, cancellationToken);
            var response = await client.SendAsync(op.Method, path, op.HasBody ? body : null, cancellationToken);
            Log(op, request, path, response.Status);
            if (!response.Ok) return ToolText.Error($"The console answered {response.Status} to {op.Method} {path.Split('?')[0]}: {response.Body}");
            return ToolText.Ok(response.Body.Length == 0 ? $"Done ({response.Status}, no content)." : response.Body);
        }
        catch (Exception e) when (e is UnifiError or UnifiNotConfigured)
        {
            return ToolText.Error(e.Message);
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
                if (p.Name == "siteId") value = await client.ResolveSiteAsync(value, ct);
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

    /// <summary>What was sent to the console: method, path and status only, never the body or arguments. <see cref="Audit.Calls"/> adds the call's outcome.</summary>
    private static void Log(ApiOperation op, RequestContext<CallToolRequestParams> request, string path, int status) =>
        Audit.Logger(request.Services)?.Log(Audit.Level(op.IsWrite), "{Login} {Tool} {Method} {Path} -> {Status}",
            Audit.Login(request.User), op.ToolName, op.Method, path.Split('?')[0], status);
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
    /// The call-tool filter: one line per call of any tool, "{Login} {Tool} -> ok", "error" (isError) or "failed" (it threw).
    /// A tool not marked read-only counts as a write.
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
