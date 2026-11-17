using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UnifiMcp;

public sealed record ApiParam(string Name, string In, bool Required, JsonObject Schema);

/// <summary>One operation of the UniFi Network API document, and the MCP tool shape generated from it.</summary>
public sealed record ApiOperation(string ToolName, string OperationId, HttpMethod Method, string Path, string Tag, string Summary,
                                  string Description, IReadOnlyList<ApiParam> Params, bool HasBody, string? BodyHint)
{
    public bool IsWrite => Method != HttpMethod.Get;

    public JsonObject InputSchema()
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in Params)
        {
            properties[p.Name] = p.Schema.DeepClone();
            if (p.Required && p.Name != "siteId") required.Add(p.Name);
        }
        if (HasBody)
        {
            properties["body"] = new JsonObject { ["type"] = "object", ["description"] = BodyHint };
            required.Add("body");
        }
        if (IsWrite)
        {
            properties["confirm"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = $"Must be exactly \"{ToolName}\" to run this change. Anything else returns a preview and sends nothing.",
            };
            required.Add("confirm");
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    /// <summary>The tool description: category prefix first, as Data-Manager's tools do, then what it does.</summary>
    public string ToolDescription() =>
        $"[{Tag}] {(IsWrite ? "WRITE: changes the live network. " : "")}{Summary}. {Description}";
}

/// <summary>
/// The pinned OpenAPI document (embedded at build) turned into a table of operations. Tools are generated from this
/// table so the server covers the API exactly and a new API version is a new document, not new code.
/// </summary>
public sealed partial class OpenApiCatalog
{
    private const int MaxInlineDepth = 12;
    private const int MaxDescription = 300;
    private readonly JsonObject _schemas;
    private readonly JsonObject _paths;

    public IReadOnlyList<ApiOperation> Operations { get; }

    private OpenApiCatalog(JsonObject doc)
    {
        _schemas = doc["components"]!["schemas"]!.AsObject();
        _paths = doc["paths"]!.AsObject();
        var ops = new List<ApiOperation>();
        foreach (var (path, item) in _paths)
            foreach (var (verb, node) in item!.AsObject())
                if (ToMethod(verb) is { } method) ops.Add(Parse(method, path, node!.AsObject()));
        Operations = ops.OrderBy(o => o.ToolName, StringComparer.Ordinal).ToList();
    }

    public static OpenApiCatalog Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("openapi.json")
            ?? throw new InvalidOperationException("The embedded OpenAPI document is missing.");
        return new OpenApiCatalog(JsonNode.Parse(stream)!.AsObject());
    }

    public ApiOperation? Find(string name) => Operations.FirstOrDefault(o => o.ToolName == name);

    /// <summary><c>getSiteOverviewPage</c> becomes <c>get_site_overview_page</c>.</summary>
    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>Full detail for one operation, with every schema reference inlined: what describe_operation returns.</summary>
    public string Describe(ApiOperation op)
    {
        var raw = _paths[op.Path]![op.Method.Method.ToLowerInvariant()]!;
        var result = new JsonObject
        {
            ["tool"] = op.ToolName,
            ["method"] = op.Method.Method,
            ["path"] = op.Path,
            ["category"] = op.Tag,
            ["summary"] = op.Summary,
            ["description"] = StripHtml(raw["description"]?.GetValue<string>() ?? ""),
            ["parameters"] = new JsonArray(op.Params.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["in"] = p.In, ["required"] = p.Required, ["schema"] = p.Schema.DeepClone(),
            }).ToArray()),
        };
        if (raw["requestBody"]?["content"]?["application/json"]?["schema"] is { } body)
            result["requestBody"] = Inline(body, [], 0);
        foreach (var code in new[] { "200", "201", "204" })
            if (raw["responses"]?[code]?["content"]?["application/json"]?["schema"] is { } resp)
            {
                result["response"] = Inline(resp, [], 0);
                break;
            }
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private ApiOperation Parse(HttpMethod method, string path, JsonObject op)
    {
        var description = op["description"]?.GetValue<string>() ?? "";
        var filterable = FilterableList(description);
        var pars = new List<ApiParam>();
        foreach (var p in op["parameters"]?.AsArray() ?? [])
        {
            var name = p!["name"]!.GetValue<string>();
            var schema = Inline(p["schema"], [], 0)!.AsObject();
            if (name == "siteId")
            {
                schema = new JsonObject { ["type"] = "string", ["description"] = "Site UUID or site name. Omit for the configured default site." };
            }
            else if (name == "filter")
            {
                schema["description"] = "Filter expression, e.g. name.like('guest*'), and(a.eq(1), b.isNotNull())." + filterable;
            }
            else if (name is "offset" or "limit")
            {
                schema["description"] = name == "limit" ? "Page size (default 25, max 200)." : "Items to skip (default 0).";
            }
            // Claude reads a parameter's description to fill it in; the document leaves most path ids bare.
            if (string.IsNullOrWhiteSpace(schema["description"]?.GetValue<string>()))
                schema["description"] = p["description"]?.GetValue<string>() is { Length: > 0 } own ? StripHtml(own) : DescribeParam(name);
            pars.Add(new ApiParam(name, p["in"]!.GetValue<string>(), p["required"]?.GetValue<bool>() == true, schema));
        }

        var bodySchema = op["requestBody"]?["content"]?["application/json"]?["schema"];
        var id = op["operationId"]!.GetValue<string>();
        var tool = ToSnakeCase(id);
        return new ApiOperation(tool, id, method, path, op["tags"]?[0]?.GetValue<string>() ?? "General",
            op["summary"]?.GetValue<string>() ?? id, Trim(StripHtml(description)), pars, bodySchema is not null,
            bodySchema is null ? null : BodyHint(bodySchema, tool));
    }

    private string BodyHint(JsonNode schema, string tool)
    {
        var resolved = schema["$ref"] is { } r ? _schemas[RefName(r.GetValue<string>())] : schema;
        var required = resolved?["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        var hint = required.Count > 0 ? $"Required fields: {string.Join(", ", required)}. " : "";
        return $"JSON request body. {hint}Call describe_operation with \"{tool}\" for the full schema.";
    }

    /// <summary>A copy with every <c>$ref</c> replaced by its schema; a schema already being expanded is named, not repeated.</summary>
    private JsonNode? Inline(JsonNode? node, HashSet<string> expanding, int depth)
    {
        switch (node)
        {
            case JsonObject o when o["$ref"] is { } reference:
                var name = RefName(reference.GetValue<string>());
                if (depth >= MaxInlineDepth || !expanding.Add(name))
                    return new JsonObject { ["description"] = $"(the '{name}' schema, already shown above)" };
                var inlined = Inline(_schemas[name], expanding, depth + 1);
                expanding.Remove(name);
                return inlined;

            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o)
                {
                    if (key == "discriminator")
                    {
                        copy["discriminator"] = value?["propertyName"]?.DeepClone();
                        var variants = new JsonObject();
                        foreach (var (variant, target) in value?["mapping"]?.AsObject() ?? [])
                            variants[variant] = Inline(new JsonObject { ["$ref"] = target!.GetValue<string>() }, expanding, depth + 1);
                        copy["variants"] = variants;
                    }
                    else
                    {
                        copy[key] = Inline(value, expanding, depth + 1);
                    }
                }
                return copy;

            case JsonArray a:
                return new JsonArray(a.Select(n => Inline(n, expanding, depth + 1)).ToArray());

            default:
                return node?.DeepClone();
        }
    }

    private static string RefName(string reference) => reference[(reference.LastIndexOf('/') + 1)..];

    private static HttpMethod? ToMethod(string verb) => verb switch
    {
        "get" => HttpMethod.Get, "post" => HttpMethod.Post, "put" => HttpMethod.Put,
        "patch" => HttpMethod.Patch, "delete" => HttpMethod.Delete, _ => null,
    };

    [GeneratedRegex(@"<details>.*?</details>", RegexOptions.Singleline)]
    private static partial Regex DetailsBlock();

    [GeneratedRegex(@"\|`([^`|]+)`\|`[^|]*`\|([^|]+)\|")]
    private static partial Regex FilterRow();

    /// <summary>A description for a parameter the document leaves bare: what it is and where its value comes from.</summary>
    internal static string DescribeParam(string name) => name switch
    {
        "portIdx" => "Port number on the device: the idx of one of its interfaces.ports.",
        "force" => "true to delete even when other configuration still refers to it.",
        _ when name.EndsWith("Id", StringComparison.Ordinal) && name.Length > 2 =>
            $"The {ToSnakeCase(name[..^2]).Replace('_', ' ')} ID (a UUID): the id field from the matching list or get tool.",
        _ => $"The {ToSnakeCase(name).Replace('_', ' ')}.",
    };

    private static string StripHtml(string text) => DetailsBlock().Replace(text, "").Trim();

    private static string Trim(string text) => text.Length <= MaxDescription ? text : text[..MaxDescription].TrimEnd() + "...";

    /// <summary>The "Filterable properties" table from a description, compacted to one line for the filter parameter.</summary>
    private static string FilterableList(string description)
    {
        var rows = FilterRow().Matches(description)
            .Select(m => $"{m.Groups[1].Value} ({m.Groups[2].Value.Replace("`", "").Trim().Replace(" ", ", ")})");
        var list = string.Join("; ", rows);
        return list.Length == 0 ? "" : " Filterable: " + list + ".";
    }
}
