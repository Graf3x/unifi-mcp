using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
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
            // Only for clients that cannot show the owner a form, and only where the owner allowed that; a client that can
            // show one asks the owner and ignores it (WriteApproval).
            properties["confirm"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Usually ignored: the server asks the owner on a confirmation form. Only when the client cannot show "
                                  + "that form and the owner set UNIFI_ALLOW_CONFIRM_FALLBACK=true: set to exactly "
                                  + $"\"{ToolName}\" to run this change; anything else returns a preview and sends nothing. "
                                  + "Without that setting, such a client is refused every write.",
            };
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

    /// <summary>
    /// Full detail for one operation, with every schema reference inlined: what describe_operation returns. Compact JSON,
    /// because indented, the firewall policy operations come to well over ToolText's cap.
    /// </summary>
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
        var body = raw["requestBody"]?["content"]?["application/json"]?["schema"];
        // A write's response is the same object its read returns; repeating it here pushed the big ones past the cap.
        var response = op.IsWrite ? null : ResponseSchema(raw);
        // Two passes: the first finds the schemas that appear more than once, the second shows each of those once, named.
        var plan = new Shape([]);
        Inline(body, plan, 0);
        Inline(response, plan, 0);
        var shape = new Shape(plan.Repeated());
        if (body is not null) result["requestBody"] = Inline(body, shape, 0);
        if (response is not null) result["response"] = Inline(response, shape, 0);
        else if (op.IsWrite && ResponseSchema(raw) is { } written) result["response"] = WriteResponse(written);
        var notes = new List<string>();
        if (shape.HasVariants)
            notes.Add("Set the discriminator property to one of the variant keys; each variant lists only the properties and "
                      + "required fields it adds to the schema it sits in.");
        if (shape.Share.Count > 0)
            notes.Add("A schema used more than once is shown in full the first time, marked \"schema\": its name, and named after that.");
        if (notes.Count > 0) result["notes"] = string.Join(" ", notes);
        return result.ToJsonString(DescribeJson);
    }

    // Not HTML, so quotes and + need no escaping; escaping them only costs room and reads worse.
    private static readonly JsonSerializerOptions DescribeJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // A schema this small reads better repeated than named.
    private const int ShareFrom = 200;

    /// <summary>The state of one render: the references being expanded, and which schemas to show once and then name.</summary>
    private sealed class Shape(HashSet<string> share)
    {
        public readonly HashSet<string> Expanding = [];
        public readonly HashSet<string> Share = share;
        public readonly HashSet<string> Shown = [];
        public readonly Dictionary<string, (int Uses, int Size)> Seen = [];
        public readonly HashSet<string> Cycled = [];
        public bool HasVariants;

        // A cycle names its ancestor, so that one needs its name on it too.
        public HashSet<string> Repeated() =>
            Seen.Where(s => s.Value.Uses > 1 && s.Value.Size >= ShareFrom).Select(s => s.Key).Concat(Cycled).ToHashSet();

        public void Saw(string name, JsonNode? node)
        {
            var (uses, size) = Seen.GetValueOrDefault(name);
            Seen[name] = (uses + 1, uses == 0 ? node?.ToJsonString(DescribeJson).Length ?? 0 : size);
        }
    }

    private static JsonNode? ResponseSchema(JsonNode raw) =>
        new[] { "200", "201", "204" }.Select(code => raw["responses"]?[code]?["content"]?["application/json"]?["schema"])
            .FirstOrDefault(s => s is not null);

    /// <summary>A write's response in one line: the read that returns the same object, or the object's top-level fields.</summary>
    private string WriteResponse(JsonNode schema)
    {
        if (schema["$ref"] is { } r && ReadsByResponse.TryGetValue(RefName(r.GetValue<string>()), out var read))
            return $"The resulting object, as {read} returns it; call describe_operation with \"{read}\" for its schema.";
        var resolved = schema["$ref"] is { } s ? _schemas[RefName(s.GetValue<string>())] : schema;
        var names = resolved?["properties"]?.AsObject().Select(p => p.Key).ToList() ?? [];
        return names.Count > 0 ? $"An object with: {string.Join(", ", names)}." : $"A {resolved?["type"]?.GetValue<string>() ?? "value"}.";
    }

    private Dictionary<string, string> ReadsByResponse => _readsByResponse ??= Operations
        .Where(o => !o.IsWrite)
        .Select(o => (o.ToolName, Schema: ResponseSchema(_paths[o.Path]!["get"]!)?["$ref"]?.GetValue<string>()))
        .Where(x => x.Schema is not null)
        .GroupBy(x => RefName(x.Schema!))
        .ToDictionary(g => g.Key, g => g.First().ToolName);
    private Dictionary<string, string>? _readsByResponse;

    private ApiOperation Parse(HttpMethod method, string path, JsonObject op)
    {
        var description = op["description"]?.GetValue<string>() ?? "";
        var filterable = FilterableList(description);
        var pars = new List<ApiParam>();
        foreach (var p in op["parameters"]?.AsArray() ?? [])
        {
            var name = p!["name"]!.GetValue<string>();
            var schema = Inline(p["schema"], new Shape([]), 0)!.AsObject();
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

    /// <summary>
    /// A copy with every <c>$ref</c> replaced by its schema; a schema already being expanded is named, not repeated.
    /// <paramref name="depth"/> counts references followed, not JSON nesting: counting nesting cut off deep variants
    /// behind an "already shown" note that was not true.
    /// </summary>
    private JsonNode? Inline(JsonNode? node, Shape shape, int depth, string? named = null)
    {
        switch (node)
        {
            case JsonObject o when o["$ref"] is { } reference:
                var name = RefName(reference.GetValue<string>());
                if (shape.Expanding.Contains(name)) shape.Cycled.Add(name);
                var stub = shape.Expanding.Contains(name) || shape.Shown.Contains(name) ? $"(the '{name}' schema, shown above)"
                    : depth >= MaxInlineDepth ? $"(the '{name}' schema, nested too deep to show here)"
                    : null;
                var inlined = new JsonObject();
                if (stub is null)
                {
                    shape.Expanding.Add(name);
                    var full = Inline(_schemas[name], shape, depth + 1, name)!.AsObject();
                    shape.Expanding.Remove(name);
                    shape.Saw(name, full);
                    if (shape.Share.Contains(name))
                    {
                        shape.Shown.Add(name);
                        inlined["schema"] = name;
                    }
                    foreach (var (key, value) in full.ToList())
                    {
                        full.Remove(key);
                        inlined[key] = value;
                    }
                }
                // Keys beside the $ref, such as what null means for this property, belong to the property, so they win.
                foreach (var (key, value) in o)
                    if (key != "$ref") inlined[key] = value?.DeepClone();
                if (stub is not null)
                    inlined["description"] = inlined["description"] is { } own ? $"{own.GetValue<string>()} {stub}" : stub;
                return inlined;

            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o)
                    if (key != "discriminator") copy[key] = Inline(value, shape, depth);
                // After the base's own fields, since each variant only lists what it adds to them.
                if (o["discriminator"] is { } discriminator)
                {
                    copy["discriminator"] = discriminator["propertyName"]?.DeepClone();
                    var variants = new JsonObject();
                    foreach (var (variant, target) in discriminator["mapping"]?.AsObject() ?? [])
                        variants[variant] = Variant(RefName(target!.GetValue<string>()), named, shape, depth);
                    copy["variants"] = variants;
                    shape.HasVariants = true;
                }
                return copy;

            case JsonArray a:
                return new JsonArray(a.Select(n => Inline(n, shape, depth)).ToArray());

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// One discriminator variant, showing only what it adds to <paramref name="owner"/>, the schema whose variants these
    /// are and which is already shown. Each variant's allOf names its base(s) and repeats their required fields; showing
    /// all that again for every variant is what pushed the biggest operations past the cap.
    /// </summary>
    private JsonNode? Variant(string name, string? owner, Shape shape, int depth)
    {
        if (owner is null || shape.Expanding.Contains(name) || depth >= MaxInlineDepth)
            return Inline(new JsonObject { ["$ref"] = $"#/components/schemas/{name}" }, shape, depth);
        var schema = _schemas[name]!.AsObject();
        var baseProperties = _schemas[owner]!["properties"]?.AsObject();
        var properties = new JsonObject();
        var required = new List<string>();
        var rest = new JsonArray();
        var result = new JsonObject();
        if (shape.Share.Contains(name)) result["schema"] = name;
        shape.Expanding.Add(name);

        // allOf means every part's fields and required list apply; keep the ones the owner does not already show the same way.
        void Take(JsonNode? part)
        {
            foreach (var (prop, sub) in part?["properties"]?.AsObject() ?? [])
                if (!properties.ContainsKey(prop) && !JsonNode.DeepEquals(sub, baseProperties?[prop]))
                    properties[prop] = Inline(sub, shape, depth + 1);
            required.AddRange(Names(part?["required"]));
        }

        if (schema["description"] is { } description) result["description"] = description.DeepClone();
        Take(schema);
        foreach (var part in schema["allOf"]?.AsArray() ?? [])
        {
            if (part?["$ref"] is not { } r) Take(part);
            // The owner, or another schema this variant is also a variant of (the metadata ones list several).
            else if (_schemas[RefName(r.GetValue<string>())] is { } other && MapsTo(other, name)) Take(other);
            else rest.Add(Inline(part, shape, depth + 1));
        }
        shape.Expanding.Remove(name);

        var ownerRequired = Names(_schemas[owner]!["required"]);
        var added = required.Distinct().Where(n => !ownerRequired.Contains(n)).ToList();
        if (properties.Count > 0) result["properties"] = properties;
        if (added.Count > 0) result["required"] = new JsonArray(added.Select(n => (JsonNode)n).ToArray());
        if (rest.Count > 0) result["allOf"] = rest;
        return result;
    }

    private static bool MapsTo(JsonNode schema, string variant) =>
        schema["discriminator"]?["mapping"]?.AsObject().Any(m => RefName(m.Value!.GetValue<string>()) == variant) == true;

    private static HashSet<string> Names(JsonNode? node) => node switch
    {
        JsonObject o => o.Select(p => p.Key).ToHashSet(),
        JsonArray a => a.Select(n => n!.GetValue<string>()).ToHashSet(),
        _ => [],
    };

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
