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
}
