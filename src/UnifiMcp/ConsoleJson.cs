using System.Globalization;
using System.Text.Json;

namespace UnifiMcp;

/// <summary>
/// Reading the console's JSON by field name: every value a tool shows is read this way, never by passing an object
/// through, because the console's objects carry secrets. Each reader takes any element and answers "nothing" for a
/// missing field, a field of another type or an element that is not an object; none of them throws.
/// </summary>
internal static class ConsoleJson
{
    /// <summary>A field as text, for showing: strings trimmed, numbers (rounded to <paramref name="decimals"/> when given) and booleans as written. Never null; empty when there is nothing to show.</summary>
    public static string Text(JsonElement e, string name, int? decimals = null)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.Trim() ?? "",
            JsonValueKind.Number when decimals is { } dp && v.TryGetDouble(out var d) => Math.Round(d, dp).ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => "",
        };
    }

    /// <summary>A string field exactly as sent, or null when there is none: for "is it there?" tests.</summary>
    public static string? TextOrNull(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A number field exactly as sent, or empty when it is not a number.</summary>
    public static string NumberText(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";

    /// <summary>A field as a number; the console sends some numbers as strings, and those count.</summary>
    public static double? Dbl(JsonElement e, string name) =>
        double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <inheritdoc cref="Dbl"/>
    public static long? Long(JsonElement e, string name) => Dbl(e, name) is { } d ? (long)d : null;

    /// <summary>True for <c>true</c>, or the string "true".</summary>
    public static bool Bool(JsonElement e, string name) => Text(e, name) == "true";

    /// <summary>A field that is an object, or null.</summary>
    public static JsonElement? Obj(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>The items of a field that is an array, or none.</summary>
    public static List<JsonElement> Items(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    /// <summary>A client by the name people know it by: its alias, else its display name, its hostname, its MAC.</summary>
    public static string ClientName(JsonElement c) =>
        new[] { Text(c, "name"), Text(c, "display_name"), Text(c, "hostname"), Text(c, "mac") }.FirstOrDefault(v => v.Length > 0) ?? "";

    /// <summary>A console's answer cut to 500 characters, for an error message.</summary>
    public static string Short(string body) => body.Length <= 500 ? body : body[..500] + "...";
}
