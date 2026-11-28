using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UnifiMcp.Tests;

public class CatalogTests
{
    private static readonly OpenApiCatalog Catalog = OpenApiCatalog.Load();

    [Fact]
    public void Every_operation_in_the_document_becomes_one_tool()
    {
        Assert.Equal(73, Catalog.Operations.Count);
        Assert.Equal(41, Catalog.Operations.Count(o => !o.IsWrite));
        Assert.Equal(32, Catalog.Operations.Count(o => o.IsWrite));
        Assert.Equal(73, Catalog.Operations.Select(o => o.ToolName).Distinct().Count());
    }

    [Theory]
    [InlineData("getSiteOverviewPage", "get_site_overview_page")]
    [InlineData("executeAdoptedDeviceAction", "execute_adopted_device_action")]
    [InlineData("getDpiApplicationCategories", "get_dpi_application_categories")]
    public void Tool_names_are_snake_case_of_the_operation_id(string id, string expected) =>
        Assert.Equal(expected, OpenApiCatalog.ToSnakeCase(id));

    [Fact]
    public void Non_get_methods_are_writes()
    {
        Assert.True(Catalog.Find("create_acl_rule")!.IsWrite);
        Assert.True(Catalog.Find("execute_port_action")!.IsWrite);
        Assert.False(Catalog.Find("get_info")!.IsWrite);
    }

    [Fact]
    public void Site_id_is_optional_but_other_path_params_are_required()
    {
        var schema = Catalog.Find("get_acl_rule")!.InputSchema();
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("aclRuleId", required);
        Assert.DoesNotContain("siteId", required);
        Assert.NotNull(schema["properties"]!["siteId"]);
    }

    [Fact]
    public void Write_tools_require_their_body_and_offer_confirm_as_the_fallback()
    {
        var schema = Catalog.Find("create_dns_policy")!.InputSchema();
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.DoesNotContain("confirm", required);
        Assert.Contains("body", required);
        var confirm = schema["properties"]!["confirm"]!["description"]!.GetValue<string>();
        Assert.Contains("cannot show", confirm);
        // It works only where the owner allowed it, and says so, so the model does not take it for a way around the form.
        Assert.Contains("UNIFI_ALLOW_CONFIRM_FALLBACK", confirm);
    }

    [Fact]
    public void Read_tools_have_no_confirm()
    {
        var schema = Catalog.Find("get_info")!.InputSchema();
        Assert.Null(schema["properties"]!["confirm"]);
    }

    [Fact]
    public void Filter_description_names_the_filterable_properties()
    {
        var filter = Catalog.Find("get_connected_client_overview_page")!.InputSchema()["properties"]!["filter"]!;
        var text = filter["description"]!.GetValue<string>();
        Assert.Contains("ipAddress", text);
        Assert.Contains("macAddress", text);
    }

    [Fact]
    public void Describe_inlines_referenced_schemas_and_discriminator_variants()
    {
        var text = Catalog.Describe(Catalog.Find("create_dns_policy")!);
        Assert.Contains("A_RECORD", text);
        Assert.Contains("ipv4Address", text);
        Assert.DoesNotContain("#/components/schemas", text);
    }

    // describe_operation puts a state line in front of Describe, and the whole answer must stay under the cap uncut.
    private const int StateLineRoom = 500;

    [Fact]
    public void Every_describe_fits_under_the_tool_text_cap()
    {
        var over = Catalog.Operations.Select(o => (o.ToolName, Catalog.Describe(o).Length))
            .Where(x => x.Length >= ToolText.Limit - StateLineRoom).ToList();
        Assert.True(over.Count == 0, string.Join(", ", over.Select(x => $"{x.ToolName} {x.Length}")));
    }

    [Fact]
    public void A_write_describe_keeps_every_property_of_its_request_body_and_every_variant()
    {
        foreach (var op in Catalog.Operations.Where(o => o.HasBody))
        {
            var text = Catalog.Describe(op);
            foreach (var name in RawProperties(RawOperation(op)["requestBody"]!["content"]!["application/json"]!["schema"]))
                Assert.True(text.Contains($"\"{name}\""), $"{op.ToolName} lost body property {name}");
        }
    }

    [Fact]
    public void A_read_describe_keeps_every_property_of_its_response()
    {
        foreach (var op in Catalog.Operations.Where(o => !o.IsWrite))
        {
            var text = Catalog.Describe(op);
            foreach (var name in RawProperties(RawOperation(op)["responses"]?["200"]?["content"]?["application/json"]?["schema"]))
                Assert.True(text.Contains($"\"{name}\""), $"{op.ToolName} lost response property {name}");
        }
    }

    [Fact]
    public void A_write_describe_points_at_the_read_for_its_response_instead_of_repeating_it()
    {
        var describe = JsonNode.Parse(Catalog.Describe(Catalog.Find("create_wifi_broadcast")!))!;
        var response = describe["response"]!.GetValue<string>();
        Assert.Contains("get_wifi_broadcast_details", response);
    }

    [Fact]
    public void A_variant_shows_only_what_it_adds_to_the_base()
    {
        var describe = JsonNode.Parse(Catalog.Describe(Catalog.Find("get_wifi_broadcast_details")!))!;
        var response = describe["response"]!;
        var standard = response["variants"]!["STANDARD"]!;
        // Base fields are in the base, once; the variant lists only its own, and its required list only its own too.
        Assert.NotNull(response["properties"]!["securityConfiguration"]);
        Assert.Null(standard["properties"]!["securityConfiguration"]);
        Assert.NotNull(standard["properties"]!["bandSteeringEnabled"]);
        var required = standard["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("broadcastingFrequenciesGHz", required);
        Assert.DoesNotContain("securityConfiguration", required);
    }

    [Fact]
    public void Every_shown_above_note_points_at_one_named_schema_before_it()
    {
        var notes = 0;
        foreach (var op in Catalog.Operations)
        {
            var text = Catalog.Describe(op);
            Assert.DoesNotContain("nested too deep", text);
            notes += Regex.Matches(text, "shown above").Count;
            var labels = Regex.Matches(text, "\"schema\":\"([^\"]+)\"").Select(m => (Name: m.Groups[1].Value, m.Index)).ToList();
            Assert.True(labels.Count == labels.Select(l => l.Name).Distinct().Count(), $"{op.ToolName} names a schema twice");
            foreach (Match note in Regex.Matches(text, @"\(the '([^']+)' schema, shown above\)"))
                Assert.True(labels.Any(l => l.Name == note.Groups[1].Value && l.Index < note.Index),
                    $"{op.ToolName} points at '{note.Groups[1].Value}', which is not named above");
        }
        Assert.True(notes > 0);
    }

    private static readonly JsonObject Raw = LoadRaw();

    private static JsonObject LoadRaw()
    {
        using var stream = typeof(OpenApiCatalog).Assembly.GetManifestResourceStream("openapi.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static JsonNode RawOperation(ApiOperation op) => Raw["paths"]![op.Path]![op.Method.Method.ToLowerInvariant()]!;

    /// <summary>
    /// Every property name reachable from a schema in the raw document, through refs, allOf and every variant. A
    /// variant's allOf names its bases; only their fields count there, not their own variants, which are other types.
    /// </summary>
    private static HashSet<string> RawProperties(JsonNode? schema)
    {
        var names = new HashSet<string>();
        var seen = new HashSet<string>();
        var bases = new HashSet<string>();
        static string RefName(JsonNode r) => r.GetValue<string>()[(r.GetValue<string>().LastIndexOf('/') + 1)..];
        static JsonNode Named(string name) => Raw["components"]!["schemas"]![name]!;
        static bool IsBaseOf(JsonNode schema, string variant) =>
            schema["discriminator"]?["mapping"]?.AsObject().Any(m => RefName(m.Value!) == variant) == true;

        void Walk(JsonNode? node, string? current)
        {
            switch (node)
            {
                case JsonObject o when o["$ref"] is { } r:
                    var name = RefName(r);
                    if (seen.Add(name)) Walk(Named(name), name);
                    break;
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "properties")
                            foreach (var (prop, sub) in value!.AsObject()) { names.Add(prop); Walk(sub, null); }
                        else if (key == "discriminator")
                            foreach (var (_, target) in value?["mapping"]?.AsObject() ?? [])
                                Walk(new JsonObject { ["$ref"] = target!.GetValue<string>() }, null);
                        else if (key == "allOf" && current is not null)
                            foreach (var part in value!.AsArray())
                                if (part?["$ref"] is { } b && IsBaseOf(Named(RefName(b)), current)) WalkBase(RefName(b));
                                else Walk(part, null);
                        else if (key is not ("example" or "required" or "enum"))
                            Walk(value, null);
                    }
                    break;
                case JsonArray a:
                    foreach (var n in a) Walk(n, null);
                    break;
            }
        }
        // A base's fields, including what it takes through its own allOf, but not its variants: those are other types.
        void WalkBase(string name)
        {
            if (!bases.Add(name)) return;
            Walk(new JsonObject { ["properties"] = Named(name)["properties"]?.DeepClone() ?? new JsonObject() }, null);
            foreach (var part in Named(name)["allOf"]?.AsArray() ?? [])
                if (part?["$ref"] is { } r) WalkBase(RefName(r));
                else Walk(part, null);
        }

        Walk(schema, null);
        return names;
    }
}
