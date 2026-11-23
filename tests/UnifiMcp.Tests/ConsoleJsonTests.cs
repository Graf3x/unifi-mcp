using System.Text.Json;
using static UnifiMcp.ConsoleJson;

namespace UnifiMcp.Tests;

public class ConsoleJsonTests
{
    private static readonly JsonElement Device = JsonDocument.Parse("""
        {"name":"  Core Switch  ","port":7,"load":12.346,"up":true,"poe":false,"cpu":"7.5","none":null,
         "uplink":{"name":"UDM"},"ports":[{"idx":1},{"idx":2}],"list":[1]}
        """).RootElement;

    private static readonly JsonElement NotAnObject = JsonDocument.Parse("[1,2]").RootElement;

    [Fact]
    public void Text_is_never_null_and_shows_strings_numbers_and_booleans()
    {
        Assert.Equal("Core Switch", Text(Device, "name"));
        Assert.Equal("7", Text(Device, "port"));
        Assert.Equal("12.35", Text(Device, "load", decimals: 2));
        Assert.Equal("true", Text(Device, "up"));
        Assert.Equal("", Text(Device, "none"));
        Assert.Equal("", Text(Device, "uplink"));
        Assert.Equal("", Text(Device, "missing"));
        Assert.Equal("", Text(NotAnObject, "name"));
    }

    [Fact]
    public void Text_or_null_is_a_string_as_sent_or_nothing()
    {
        Assert.Equal("  Core Switch  ", TextOrNull(Device, "name"));
        Assert.Null(TextOrNull(Device, "port"));
        Assert.Null(TextOrNull(Device, "missing"));
        Assert.Null(TextOrNull(NotAnObject, "name"));
    }

    [Fact]
    public void Number_text_is_a_number_as_sent_or_empty()
    {
        Assert.Equal("12.346", NumberText(Device, "load"));
        Assert.Equal("", NumberText(Device, "cpu"));
        Assert.Equal("", NumberText(NotAnObject, "load"));
    }

    [Fact]
    public void Numbers_and_flags_read_strings_that_hold_them()
    {
        Assert.Equal(7.5, Dbl(Device, "cpu"));
        Assert.Equal(12, Long(Device, "load"));
        Assert.Null(Dbl(Device, "name"));
        Assert.True(Bool(Device, "up"));
        Assert.False(Bool(Device, "poe"));
        Assert.False(Bool(Device, "missing"));
    }

    [Fact]
    public void Objects_and_items_are_found_only_where_they_are()
    {
        Assert.Equal("UDM", Text(Obj(Device, "uplink")!.Value, "name"));
        Assert.Null(Obj(Device, "ports"));
        Assert.Null(Obj(null, "uplink"));
        Assert.Equal(2, Items(Device, "ports").Count);
        Assert.Empty(Items(Device, "uplink"));
        Assert.Empty(Items(NotAnObject, "ports"));
        Assert.Empty(Items(null, "ports"));
    }

    [Fact]
    public void A_long_console_body_is_shortened_for_an_error()
    {
        Assert.Equal("short", Short("short"));
        var shortened = Short(new string('b', 600));
        Assert.Equal(503, shortened.Length);
        Assert.EndsWith("...", shortened);
    }
}
