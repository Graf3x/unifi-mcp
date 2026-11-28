using System.Text;
using System.Text.RegularExpressions;

namespace UnifiMcp.Tests;

/// <summary>
/// The README's table of write tools, the names an owner picks from for UNIFI_WRITE_TOOLS. It is generated here, not in
/// the server: only the docs use it, and a change under src/UnifiMcp would also mean regenerating deploy/SHA256SUMS.
/// </summary>
public class WriteToolsReadmeTests
{
    private const string Start = "<!-- write-tools:start -->";
    private const string End = "<!-- write-tools:end -->";
    private static readonly OpenApiCatalog Catalog = OpenApiCatalog.Load();

    /// <summary>One row per write tool, by category then name, as it goes between the README's markers.</summary>
    internal static string WriteToolsMarkdown(OpenApiCatalog catalog)
    {
        var sb = new StringBuilder();
        sb.Append("| Tool | Category | What it does | Deletes |\n|---|---|---|---|\n");
        foreach (var op in catalog.Operations.Where(o => o.IsWrite)
                     .OrderBy(o => o.Tag, StringComparer.Ordinal).ThenBy(o => o.ToolName, StringComparer.Ordinal))
            sb.Append($"| `{op.ToolName}` | {Cell(op.Tag)} | {Cell(op.Summary)} | {(op.Method == HttpMethod.Delete ? "yes" : "no")} |\n");
        return sb.ToString().TrimEnd('\n');
    }

    // A pipe in a summary would end the cell early, and a line break the row.
    private static string Cell(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|").Trim();

    [Fact]
    public void The_readme_lists_every_write_tool_as_generated()
    {
        var expected = WriteToolsMarkdown(Catalog);
        var block = ReadmeBlock();
        // The message carries the whole block, so a contributor can paste it between the markers.
        Assert.True(block == expected,
            $"README.md's write tools table is out of date. Replace everything between {Start} and {End} with:\n\n{expected}");
    }

    [Fact]
    public void The_readme_table_has_one_real_write_tool_per_row()
    {
        var names = Regex.Matches(ReadmeBlock(), @"(?m)^\| `([a-z0-9_]+)` \|").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(Catalog.Operations.Count(o => o.IsWrite), names.Count);
        Assert.All(names, n => Assert.True(Catalog.Find(n)?.IsWrite == true, $"{n} is not a write tool"));
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    private static string ReadmeBlock()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md")).Replace("\r\n", "\n");
        var from = readme.IndexOf(Start, StringComparison.Ordinal);
        var to = readme.IndexOf(End, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from,
            $"README.md needs {Start} and {End} in its Writes section, with this between them:\n\n{WriteToolsMarkdown(Catalog)}");
        // Blank lines around the table keep it apart from the markers; they are not part of it.
        return readme[(from + Start.Length)..to].Trim('\n');
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "unifi-mcp.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
