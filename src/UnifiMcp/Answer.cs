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

    private readonly StringBuilder _text = new();

    private Answer() { }

    /// <summary>The view's data, serialised with camelCase names and without nulls. Null for no view.</summary>
    public object? View { get; set; }

    /// <summary>
    /// Runs <paramref name="body"/>: one text block, capped at <see cref="ToolText.Limit"/>, and the view if one was set.
    /// When the answer ends early the view is <paramref name="onFatal"/>'s, given the reason.
    /// </summary>
    public static async Task<CallToolResult> RunAsync(Func<Answer, Task> body, Func<string, object>? onFatal = null)
    {
        var answer = new Answer();
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
        return new CallToolResult
        {
            IsError = fatal,
            Content = [new TextContentBlock { Text = ToolText.Cap(answer._text.ToString()) }],
            Meta = answer.View is { } view ? new JsonObject { [ViewResources.ViewMetaKey] = JsonSerializer.SerializeToNode(view, view.GetType(), ViewJson) } : null,
        };
    }

    public Answer Write(string text)
    {
        _text.Append(text);
        return this;
    }

    public Answer Line(string text = "")
    {
        _text.AppendLine(text);
        return this;
    }

    /// <summary>One read; when the console refuses it the answer says so and null comes back.</summary>
    public async Task<T?> Step<T>(string name, Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (UnifiError e)
        {
            Failed(name, e);
            return null;
        }
    }

    /// <inheritdoc cref="Step{T}"/>
    public async Task<JsonElement?> Step(string name, Func<Task<JsonElement>> read)
    {
        try { return await read(); }
        catch (UnifiError e)
        {
            Failed(name, e);
            return null;
        }
    }

    public override string ToString() => _text.ToString();

    /// <summary>A markdown table. A pipe in a cell is escaped so it cannot end the cell.</summary>
    public static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder($"| {string.Join(" | ", headers)} |\n|{string.Concat(headers.Select(_ => "---|"))}\n");
        foreach (var row in rows) sb.AppendLine($"| {string.Join(" | ", row.Select(c => c.Replace("|", "\\|")))} |");
        return sb.ToString();
    }

    private void Failed(string name, Exception e) => Line($"**{name} step failed:** {e.Message}").Line();
}
