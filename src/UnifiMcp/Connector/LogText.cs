using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace UnifiMcp;

/// <summary>Client-supplied text on its way into a log line.</summary>
internal static class LogText
{
    /// <summary>
    /// Drops control characters (C0, DEL, C1, which take in CR, LF and NEL) and the Unicode line and paragraph
    /// separators, so a value cannot end its log line and forge the next one.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Clean(string? value)
    {
        if (value is null || !value.Any(Unsafe)) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            if (!Unsafe(c)) sb.Append(c);
        return sb.ToString();
    }

    private static bool Unsafe(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';
}
