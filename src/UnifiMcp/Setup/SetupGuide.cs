using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace UnifiMcp;

/// <summary>
/// The README's Setup section as HTML for the setup page, so the page and the README never disagree. Rendered once at
/// startup from the README compiled into the assembly: its own trusted content, so its raw HTML (picture, details) passes
/// through. Links that leave the section go to the README on GitHub, diagrams are served by the page, and the README's
/// placeholders (the console address, the public host name) are replaced by this server's values where it has them.
/// </summary>
public sealed partial class SetupGuide
{
    public const string RepoUrl = "https://github.com/Graf3x/unifi-mcp";
    private const string ReadmeResource = "setup/README.md";
    private const string ImagePrefix = "setup/images/";

    /// <summary>One part of the section: the text before the first guide, or one h3 and what follows it.</summary>
    public sealed record Part(string Id, string Title, string Html);

    /// <summary>The section in parts, or empty when this build carries no README (the owner's Fleet bundle).</summary>
    public IReadOnlyList<Part> Parts { get; }

    public SetupGuide(Settings settings)
    {
        var markdown = ReadResource(ReadmeResource);
        Parts = markdown is null || Section(markdown) is not { } section ? [] : Split(Rewrite(Render(section), settings));
    }

    public SetupGuide(string readme, Settings settings) =>
        Parts = Section(readme) is { } section ? Split(Rewrite(Render(section), settings)) : [];

    /// <summary>
    /// Which guide this server's settings follow: 4 LAN (MCP_LAN_LISTEN, no public URL), 1 local (a TCP MCP_LISTEN, no public
    /// URL), 3 Google sign-in, else 2.
    /// </summary>
    public static int GuideOf(Settings s) =>
        s.PublicBaseUrl is null && s.LanListen is not null ? 4
        : Listeners.UnixPath(s.Listen) is null && s.PublicBaseUrl is null ? 1
        : s.SignIn == SignInMode.Google ? 3 : 2;

    /// <summary>A diagram from docs/images, by file name; null for anything else.</summary>
    public static byte[]? Image(string name)
    {
        if (!SafeImageName().IsMatch(name)) return null;
        using var stream = typeof(SetupGuide).Assembly.GetManifestResourceStream(ImagePrefix + name);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    internal static string? ReadResource(string name, Assembly? assembly = null)
    {
        using var stream = (assembly ?? typeof(SetupGuide).Assembly).GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>From "## Setup" to the next level-two heading, outside code fences. The heading itself is left out.</summary>
    internal static string? Section(string readme)
    {
        var lines = readme.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        var fence = false;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) fence = !fence;
            if (!fence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (start >= 0) break;
                if (line.Trim() == "## Setup") start = i;
                continue;
            }
            if (start >= 0) sb.Append(line).Append('\n');
        }
        return start < 0 ? null : sb.ToString();
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UsePipeTables()
        .Build();

    private static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    /// <summary>Image paths, links out of the section, and the placeholders, in that order.</summary>
    internal static string Rewrite(string html, Settings settings)
    {
        var ids = HeadingId().Matches(html).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        html = html.Replace("srcset=\"docs/images/", "srcset=\"/setup/images/", StringComparison.Ordinal)
                   .Replace("src=\"docs/images/", "src=\"/setup/images/", StringComparison.Ordinal);
        html = Href().Replace(html, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups[1].Value);
            string target;
            if (url.StartsWith('#')) target = ids.Contains(url[1..]) ? url : RepoUrl + url;
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) target = url;
            else target = $"{RepoUrl}/blob/main/{url.TrimStart('.', '/')}";
            var external = !target.StartsWith('#');
            return $"href=\"{WebUtility.HtmlEncode(target)}\"" + (external ? " rel=\"noopener\" target=\"_blank\"" : "");
        });
        return Placeholders(html, settings);
    }

    /// <summary>
    /// The README's placeholders, in text only (never inside a tag), marked so the page can show they are this server's.
    /// Left alone when the server still has the README's own value, or none.
    /// </summary>
    internal static string Placeholders(string html, Settings settings)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var console = settings.BaseUrl;
        if (!string.Equals(console.Host, "192.168.1.1", StringComparison.Ordinal) || !console.IsDefaultPort)
        {
            map["https://192.168.1.1"] = console.GetLeftPart(UriPartial.Authority);
            map["192.168.1.1:443"] = $"{console.Host}:{console.Port}";
            map["192.168.1.1"] = console.Host;
        }
        if (settings.PublicBaseUrl is { } pub && !string.Equals(pub.Host, "unifi.example.com", StringComparison.OrdinalIgnoreCase))
            map["unifi.example.com"] = pub.IsDefaultPort ? pub.Host : $"{pub.Host}:{pub.Port}";
        if (map.Count == 0) return html;

        var pattern = new Regex(string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)));
        var sb = new StringBuilder(html.Length + 256);
        foreach (var piece in TagOrText().Split(html))
        {
            if (piece.StartsWith('<')) { sb.Append(piece); continue; }
            sb.Append(pattern.Replace(piece, m => $"<mark class=\"yours\" title=\"Your value\">{WebUtility.HtmlEncode(map[m.Value])}</mark>"));
        }
        return sb.ToString();
    }

    /// <summary>Cuts the section at each h3, keeping its id and text for the page's tabs.</summary>
    internal static IReadOnlyList<Part> Split(string html)
    {
        var parts = new List<Part>();
        var starts = H3().Matches(html).ToList();
        var first = starts.Count == 0 ? html.Length : starts[0].Index;
        if (first > 0) parts.Add(new("intro", "Overview", html[..first]));
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
            var title = WebUtility.HtmlDecode(Tags().Replace(starts[i].Groups[2].Value, ""));
            parts.Add(new(starts[i].Groups[1].Value, title, html[starts[i].Index..end]));
        }
        return parts;
    }

    [GeneratedRegex("^[a-z0-9-]+\\.svg$")]
    private static partial Regex SafeImageName();

    [GeneratedRegex("<h[1-6] id=\"([^\"]+)\"")]
    private static partial Regex HeadingId();

    [GeneratedRegex("href=\"([^\"]*)\"")]
    private static partial Regex Href();

    [GeneratedRegex("(<[^>]*>)")]
    private static partial Regex TagOrText();

    [GeneratedRegex("<h3 id=\"([^\"]+)\">(.*?)</h3>", RegexOptions.Singleline)]
    private static partial Regex H3();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
