namespace UnifiMcp.Tests;

/// <summary>Which write operations are enabled writes: the one place every tool and the startup line ask.</summary>
public class EnabledWritesTests
{
    private static readonly OpenApiCatalog Catalog = OpenApiCatalog.Load();
    private static readonly ApiOperation Read = Catalog.Find("get_info")!;
    private static readonly ApiOperation Listed = Catalog.Find("create_dns_policy")!;
    private static readonly ApiOperation Unlisted = Catalog.Find("delete_dns_policy")!;

    private static EnabledWrites Of(bool writes, params string[]? only) =>
        EnabledWrites.From(new Settings { AllowWrites = writes, WriteTools = only }, Catalog);

    [Fact]
    public void A_read_is_a_read_whatever_the_settings()
    {
        Assert.Equal(WriteState.Read, Of(writes: false).StateOf(Read));
        Assert.Equal(WriteState.Read, Of(writes: true).StateOf(Read));
        Assert.Equal(WriteState.Read, Of(writes: true, "create_dns_policy").StateOf(Read));
    }

    [Fact]
    public void With_writes_off_no_write_is_enabled_list_or_not()
    {
        foreach (var writes in new[] { Of(writes: false), Of(writes: false, "create_dns_policy") })
        {
            Assert.Empty(writes.Operations);
            Assert.Equal(WriteState.WritesOff, writes.StateOf(Listed));
            Assert.Equal(WriteState.WritesOff, writes.StateOf(Unlisted));
        }
    }

    [Fact]
    public void With_writes_on_and_no_list_every_write_is_enabled()
    {
        var writes = Of(writes: true, null);
        Assert.False(writes.Limited);
        Assert.Equal(writes.AllWrites, writes.Operations.Count);
        Assert.All(Catalog.Operations.Where(o => o.IsWrite), o => Assert.Equal(WriteState.Enabled, writes.StateOf(o)));
    }

    [Fact]
    public void With_a_list_only_the_named_writes_are_enabled_and_a_repeated_name_counts_once()
    {
        var writes = Of(writes: true, "create_dns_policy", "create_dns_policy");
        Assert.True(writes.Limited);
        Assert.Equal(WriteState.Enabled, writes.StateOf(Listed));
        Assert.Equal(WriteState.NotListed, writes.StateOf(Unlisted));
        Assert.Equal(new[] { "create_dns_policy" }, writes.Operations.Select(o => o.ToolName));
    }

    [Fact]
    public void All_writes_counts_the_catalog_s_write_operations() =>
        Assert.Equal(Catalog.Operations.Count(o => o.IsWrite), Of(writes: false).AllWrites);

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // a typo shows before writes are turned on
    public void A_name_that_is_not_a_write_tool_is_refused_with_writes_on_or_off(bool on)
    {
        var e = Assert.Throws<ArgumentException>(() => Of(on, "create_dns_polcy"));
        Assert.Contains("UNIFI_WRITE_TOOLS", e.Message);
        Assert.Contains("'create_dns_polcy'", e.Message);
    }
}
