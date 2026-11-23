using System.Text;
using System.Text.Json;

namespace UnifiMcp;

/// <summary>
/// The answer of a composite tool: markdown written in steps. A step that fails is reported in the answer and the rest
/// still run; a failure outside a step (not configured, an unknown site, a read the tool cannot do without) ends the
/// answer after what was already written. Exceptions other than the console's are not answers and are not caught.
/// </summary>
/// <remarks>The answer does not look up a site: a tool finds its own, as the first read outside a step.</remarks>
internal sealed class Answer
{
    private readonly StringBuilder _text = new();

    private Answer() { }

    /// <summary>Runs <paramref name="body"/> and returns its text, capped at <see cref="ToolText.Limit"/>.</summary>
    public static async Task<string> RunAsync(Func<Answer, Task> body)
    {
        var answer = new Answer();
        try
        {
            await body(answer);
        }
        catch (Exception e) when (e is UnifiError or UnifiNotConfigured)
        {
            answer.Line(e.Message);
        }
        return ToolText.Cap(answer._text.ToString());
    }

    /// <summary>Tests and the view's runner only: an answer to write into directly.</summary>
    internal static Answer Start() => new();

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
