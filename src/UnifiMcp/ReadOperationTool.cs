using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>
/// Every read operation of the API behind one tool, so the tool list stays short: Claude chooses tools less well past
/// 30 to 50 of them. Writes keep a tool each (destructive, with a confirm), because one tool must never mix safe and
/// unsafe methods. The operation names are an enum, so a known lookup needs no search first.
/// </summary>
public sealed class ReadOperationTool(OpenApiCatalog catalog, UnifiClient client) : McpServerTool
{
    public const string ToolName = "call_read_operation";

    private readonly List<ApiOperation> _reads = catalog.Operations.Where(o => !o.IsWrite).ToList();

    public override Tool ProtocolTool => _tool ??= new()
    {
        Name = ToolName,
        Title = "Run a read operation",
        Description = "[Start] Runs one read-only UniFi Network API operation and returns the console's JSON. "
                      + "Pick operation from the list (search_operations finds one by keyword; describe_operation shows its parameters and response). "
                      + "Pass its parameters in arguments by name. siteId is optional everywhere. Lists take offset, limit (max 200) and filter. "
                      + "Categories: " + string.Join(", ", _reads.Select(o => o.Tag).Distinct().Order(StringComparer.Ordinal)) + ". "
                      + "Changes are separate tools, listed only when writes are on.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The operation's name, e.g. get_info or get_dns_policy_page.",
                    ["enum"] = new JsonArray(_reads.Select(o => (JsonNode)o.ToolName).ToArray()),
                },
                ["arguments"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "The operation's parameters by name, e.g. {\"filter\": \"name.like('guest*')\", \"limit\": 50}. Omit for none.",
                    ["additionalProperties"] = true,
                },
            },
            ["required"] = new JsonArray("operation"),
            ["additionalProperties"] = false,
        }),
        Annotations = new ToolAnnotations
        {
            Title = "Run a read operation",
            ReadOnlyHint = true,
            DestructiveHint = false,
            IdempotentHint = true,
            OpenWorldHint = false,
        },
    };

    private Tool? _tool;

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var given = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        var name = given.TryGetValue("operation", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
        if (catalog.Find(name) is not { } op)
            return ToolText.Error($"No operation named '{name}'. Use search_operations to find one.");
        if (op.IsWrite)
            return ToolText.Error($"{op.ToolName} changes the network, so it is not run here. Use its own tool, {op.ToolName}, which is listed only when writes are on and asks for a confirm.");

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (given.TryGetValue("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject()) args[p.Name] = p.Value;
        else if (given.TryGetValue("arguments", out a) && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return ToolText.Error("arguments must be an object of the operation's parameters by name.");
        // Parameters given beside operation instead of inside arguments are taken as meant.
        foreach (var (key, value) in given)
            if (key is not ("operation" or "arguments")) args.TryAdd(key, value);

        var valid = op.Params.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = args.Keys.Where(k => !valid.Contains(k)).ToList();
        if (unknown.Count > 0)
            return ToolText.Error($"{op.ToolName} has no parameter {string.Join(", ", unknown.Select(u => $"'{u}'"))}. "
                                  + (valid.Count == 0 ? "It takes none." : $"It takes: {string.Join(", ", op.Params.Select(p => p.Required && p.Name != "siteId" ? p.Name + " (required)" : p.Name))}."));

        return await ApiTool.RunAsync(op, client, args, request, cancellationToken);
    }
}
