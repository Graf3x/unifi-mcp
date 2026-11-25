using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace UnifiMcp;

/// <summary>
/// The answer of a composite tool: markdown written in steps. A step that fails is reported in the answer and the rest
/// still run; a failure outside a step (not configured, an unknown site, a read the tool cannot do without) ends the
/// answer after what was already written, and is a tool error. Exceptions other than the console's are not answers and
/// are not caught.
/// </summary>
/// <remarks>
/// The answer does not look up a site: a tool finds its own, as the first read outside a step. A tool with a view sets
/// <see cref="View"/>; it travels in <c>_meta</c>, not structuredContent, because hosts hand the model structuredContent
/// in place of the text when there is one.
/// </remarks>
internal sealed class Answer
{
    private static readonly JsonSerializerOptions ViewJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The most a view's data may take in <c>_meta</c>, as UTF-8 JSON (256 KiB).</summary>
    public const int ViewLimit = 256 * 1024;

    private readonly StringBuilder _text = new();

    private Answer() { }

    /// <summary>The view's data, serialised with camelCase names and without nulls. Null for no view.</summary>
    public object? View { get; set; }

    /// <summary>
    /// Runs <paramref name="body"/>: one text block, capped at <see cref="ToolText.Limit"/>, and the view if one was set.
    /// When the answer ends early the view is <paramref name="onFatal"/>'s, given the reason.
    /// </summary>
    /// <remarks>
    /// Every read the body makes is charged to one <see cref="UpstreamBudget"/>. When a read was cut short the text starts
    /// with a "Partial answer" line that says why and how to narrow the question, ahead of the cap so it is never cut off.
    /// A view larger than <see cref="ViewLimit"/> is replaced by <paramref name="onFatal"/>'s, given why, or left out.
    /// </remarks>
    public static async Task<CallToolResult> RunAsync(Func<Answer, Task> body, Func<string, object>? onFatal = null)
    {
        var answer = new Answer();
        var budget = UpstreamBudget.Open();
        var fatal = false;
        try
        {
            await body(answer);
        }
        catch (Exception e) when (e is UnifiError or UnifiNotConfigured)
        {
            fatal = true;
            answer.Line(e.Message);
            answer.View = onFatal?.Invoke(e.Message);
        }

        var meta = ViewMeta(answer.View, onFatal, budget);
        var notes = budget.Notes;
        var text = answer._text.ToString();
        if (notes.Count > 0) text = $"**Partial answer:** {string.Join(" ", notes)}\n\n{text}";
        return new CallToolResult
        {
            IsError = fatal,
            Content = [new TextContentBlock { Text = ToolText.Cap(text) }],
            Meta = meta is null ? null : new JsonObject { [ViewResources.ViewMetaKey] = meta },
        };
    }

    /// <summary>The view's data for <c>_meta</c>, held to <see cref="ViewLimit"/>: past it the fallback view, given why, or none.</summary>
    private static JsonNode? ViewMeta(object? view, Func<string, object>? fallback, UpstreamBudget budget)
    {
        if (view is null) return null;
        var json = JsonSerializer.SerializeToUtf8Bytes(view, view.GetType(), ViewJson);
        if (json.Length <= ViewLimit) return JsonNode.Parse(json);

        var why = $"The view's data was too large to send ({json.Length / 1024} KB, at most {ViewLimit / 1024} KB). Ask about one device, client or network to see it.";
        budget.Note(why);
        if (fallback?.Invoke(why) is not { } small) return null;
        var smaller = JsonSerializer.SerializeToUtf8Bytes(small, small.GetType(), ViewJson);
        return smaller.Length <= ViewLimit ? JsonNode.Parse(smaller) : null;
    }

    // Text past the cap is cut off anyway, so it is not kept.
    public Answer Write(string text)
    {
        if (_text.Length <= ToolText.Limit) _text.Append(text);
        return this;
    }

    public Answer Line(string text = "")
    {
        if (_text.Length <= ToolText.Limit) _text.AppendLine(text);
        return this;
    }

    /// <summary>
    /// One read; when the console refuses it, or answers in a shape this server does not expect, the answer says so and
    /// null comes back.
    /// </summary>
    public async Task<T?> Step<T>(string name, Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (Exception e) when (Failed(name, e))
        {
            return null;
        }
    }

    /// <inheritdoc cref="Step{T}"/>
    public async Task<JsonElement?> Step(string name, Func<Task<JsonElement>> read)
    {
        try { return await read(); }
        catch (Exception e) when (Failed(name, e))
        {
            return null;
        }
    }

    public override string ToString() => _text.ToString();

    /// <summary>
    /// A markdown table, with <c>\n</c> line endings. A pipe in a header or cell is escaped and a line break becomes a
    /// space, so no value (a device's name, say) can end a cell or a row.
    /// </summary>
    public static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        static string Row(IEnumerable<string> cells) => $"| {string.Join(" | ", cells.Select(Cell))} |\n";
        static string Cell(string c) => c.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|");

        var sb = new StringBuilder(Row(headers)).Append('|').Append(string.Concat(headers.Select(_ => "---|"))).Append('\n');
        foreach (var row in rows) sb.Append(Row(row));
        return sb.ToString();
    }

    /// <summary>
    /// Writes the failure line for the console's refusal or for an answer of the wrong shape (what reading an
    /// unexpected JSON value throws) and says the step is over; any other exception is a bug and is left to escape.
    /// </summary>
    private bool Failed(string name, Exception e)
    {
        var reason = e switch
        {
            UnifiError => e.Message,
            InvalidOperationException or KeyNotFoundException or FormatException => "The console's answer was not what this server expects.",
            _ => null,
        };
        if (reason is null) return false;
        Line($"**{name} step failed:** {reason}").Line();
        return true;
    }
}
