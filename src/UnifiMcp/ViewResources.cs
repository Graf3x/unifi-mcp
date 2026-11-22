using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>
/// Interactive views (MCP Apps, extension io.modelcontextprotocol/ui). A view is one HTML file embedded in the assembly
/// and served as a static resource; the tool that owns it names the URI in its <c>_meta.ui.resourceUri</c>. Serving a
/// view makes no console call, and a view declares no CSP domain and no permission: it loads nothing from the network.
/// </summary>
public static class ViewResources
{
    public const string DeviceDiagnosticsUri = "ui://unifi-network/device-diagnostics.html";
    public const string MimeType = "text/html;profile=mcp-app";
    /// <summary>The key in a tool result's _meta that carries the view's data, where the model never sees it.</summary>
    public const string ViewMetaKey = "unifi-network/view";

    /// <summary>An embedded view's HTML, by the logical name given in the project file.</summary>
    internal static string Html(string logicalName)
    {
        using var stream = typeof(ViewResources).Assembly.GetManifestResourceStream(logicalName)
                           ?? throw new InvalidOperationException($"The embedded view '{logicalName}' is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>A static resource at <paramref name="uri"/> that answers with the embedded file, read once.</summary>
    internal static McpServerResource Create(string uri, string name, string title, string logicalName)
    {
        var html = Html(logicalName);
        return McpServerResource.Create(() => new TextResourceContents { Uri = uri, MimeType = MimeType, Text = html },
            new McpServerResourceCreateOptions { UriTemplate = uri, Name = name, Title = title, MimeType = MimeType });
    }

    internal static IEnumerable<McpServerResource> All() =>
        [Create(DeviceDiagnosticsUri, "device-diagnostics", "Device diagnostics view", "views/device-diagnostics.html")];
}
