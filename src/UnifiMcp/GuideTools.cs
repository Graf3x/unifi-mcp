using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnifiMcp;

/// <summary>Onboarding and discovery: how Claude finds its way around before calling the API.</summary>
[McpServerToolType]
public sealed class GuideTools(Settings settings, UnifiClient client, OpenApiCatalog catalog, EnabledWrites enabled)
{
    [McpServerTool(Name = "getting_started", Title = "Getting started", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Start] Read this first: whether the server is configured, whether writes are on, conventions for siteId, filters and paging, and which tools to use. Makes no calls to the console.")]
    public string GettingStarted()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# UniFi Network MCP").AppendLine();
        sb.AppendLine(StateLine()).AppendLine();
        sb.AppendLine("## Start here");
        sb.AppendLine("- `who_is_on_network_faq`: clients grouped by network.");
        sb.AppendLine("- `client_path_faq`: the device path a client uses to reach the gateway.");
        sb.AppendLine("- `dhcp_reservation_faq`: does a client hold the address you expected.");
        sb.AppendLine("- `network_health_faq`: offline devices, firmware, client counts.");
        sb.AppendLine("- `system_log`: what happened and when (connects, roams, threats, admin logins, WAN loss); filter by category, event, severity or search.");
        sb.AppendLine("- `internet_health_faq`: WAN links, failover, latency and packet-loss history, outages, speed tests.");
        sb.AppendLine("- `wifi_health_faq`: every AP radio's channel, utilisation, interference, retries and neighbours.");
        sb.AppendLine("- `client_diagnostics_faq`: one client's switch port or Wi-Fi signal, fixed IP and recent events, online or not.");
        sb.AppendLine("- `device_diagnostics_faq`: device CPU, memory, temperatures, PoE and per-port errors, drops and flapping.");
        sb.AppendLine("- `list_port_forwards`: port forwarding rules.");
        sb.AppendLine("- `call_read_operation`: run any of the API's read operations by name (e.g. `get_info`, `get_dns_policy_page`); `search_operations` finds one and `describe_operation` shows its parameters and schema.").AppendLine();
        sb.AppendLine("## Conventions");
        sb.AppendLine("- `siteId` is optional; it takes a UUID or site name and defaults to the configured site.");
        sb.AppendLine("- Lists take `offset`, `limit` (max 200) and `filter`, e.g. `name.like('guest*')` or `and(a.eq(1), b.isNotNull())`. The `filter` parameter lists the properties each list supports.");
        sb.AppendLine("- `call_read_operation` and the write tools return the console's JSON; `*_faq` tools return markdown in steps, and a failed step does not stop the rest.");
        sb.AppendLine("- Tool descriptions start with their category, e.g. `[Firewall]`.");
        sb.AppendLine($"- The diagnostics tools (system_log and the last five above) read the console's internal API, which its own UI uses. It is limited to {ConsoleReads.PerMinute} calls a minute (bursts of {ConsoleReads.Burst}) and repeats within {ConsoleReads.CacheFor.TotalSeconds:0} s are served from cache, because heavy polling can destabilise the console. Do not loop on them.").AppendLine();
        sb.AppendLine("## Changes");
        var writes = enabled.AllWrites;
        var listed = !enabled.Limited
            ? $"{writes} write tools are listed"
            : $"{enabled.Operations.Count} of {writes} write tools are listed, because the owner limited them with UNIFI_WRITE_TOOLS; the others cannot be called, and `describe_operation` still shows them";
        sb.AppendLine(!enabled.On
            ? $"This server is read-only: {writes} write operations exist but are not listed, because UNIFI_ALLOW_WRITES is off. `describe_operation` still shows them."
            : $"Writes are on: {listed}. Each is marked destructive and asks the owner to approve it on a confirmation form that shows the whole change; a change too big for the form is refused, so make smaller changes. "
              + (settings.AllowConfirmFallback
                  ? "UNIFI_ALLOW_CONFIRM_FALLBACK is on: a client that cannot show the form runs a write when `confirm` is set to its own tool name (without it you get a preview and nothing is sent). Show the owner the preview and wait for their yes before confirming."
                  : "A client that cannot show the form is refused every write, and `confirm` does not change that; the owner can allow it with UNIFI_ALLOW_CONFIRM_FALLBACK=true."));
        return sb.ToString();
    }

    [McpServerTool(Name = "unifi_status", Title = "Connection status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Start] Configuration and connection status: what is still missing, and when configured the console's application version and the resolved default site.")]
    public async Task<string> Status(CancellationToken ct)
    {
        var sb = new StringBuilder(StateLine()).AppendLine();
        if (settings.Missing().Count > 0) return sb.ToString();
        try
        {
            var info = await client.GetJsonAsync("/v1/info", ct);
            sb.AppendLine($"- Console application version: {info.GetProperty("applicationVersion").GetString()}");
            sb.AppendLine($"- Default site '{settings.Site}': {(await client.FindSiteAsync(null, ct)).Id}");
        }
        catch (Exception e) when (e is UnifiError or UnifiNotConfigured or KeyNotFoundException)
        {
            sb.AppendLine($"- Could not query the console: {e.Message}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "search_operations", Title = "Search API operations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Start] Search all UniFi Network API operations by keyword across name, category and summary, with each one's parameters (required ones starred) and how to run it. Includes write operations that are currently disabled.")]
    public string Search([Description("Keyword, e.g. 'dns', 'firewall', 'port'. Words must all match.")] string query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = catalog.Operations
            .Where(o => words.All(w => $"{o.ToolName} {o.Tag} {o.Summary}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(40).ToList();
        if (hits.Count == 0) return $"No operation matches '{query}'.";
        return Answer.Table(["operation", "method", "category", "summary", "parameters", "run with"], hits.Select(o => new string[]
        {
            o.ToolName, $"{o.Method}", o.Tag, o.Summary,
            string.Join(", ", o.Params.Where(p => p.Name != "siteId").Select(p => p.Required ? p.Name + "*" : p.Name).Concat(o.HasBody ? ["body*"] : [])),
            RunWith(o),
        }));
    }

    private string RunWith(ApiOperation o) =>
        !o.IsWrite ? $"{ReadOperationTool.ToolName}" : Disabled(o) ?? $"its own tool {o.ToolName} (destructive, needs the owner's approval)";

    /// <summary>Why a write has no tool, or null when it has one.</summary>
    private string? Disabled(ApiOperation o) => enabled.StateOf(o) switch
    {
        WriteState.WritesOff => "disabled (writes off)",
        WriteState.NotListed => "disabled (not in UNIFI_WRITE_TOOLS)",
        _ => null,
    };

    [McpServerTool(Name = "describe_operation", Title = "Describe an API operation", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("[Start] Full detail for one operation: parameters plus the complete request body schema, and for a read its complete response schema, with references inlined. A write names the read that returns its result, or lists the result's fields. Use before composing a write body.")]
    public CallToolResult Describe([Description("Tool name, e.g. 'create_dns_policy'.")] string operation)
    {
        if (catalog.Find(operation ?? "") is not { } op)
            return ToolText.Error($"No operation named '{operation}'. Use search_operations to find one.");
        var how = op.IsWrite ? RunWith(op) : $"{ReadOperationTool.ToolName} with operation \"{op.ToolName}\" and its parameters in arguments";
        return ToolText.Ok($"State: {StateOf(op)}. Run with: {how}.\n\n{catalog.Describe(op)}");
    }

    private string StateOf(ApiOperation o) => !o.IsWrite ? "read" : Disabled(o) ?? "write";

    private string StateLine()
    {
        var missing = settings.Missing();
        var state = missing.Count == 0 ? "Configured." : "**The server is not configured**, so it will not call the console. Still needed:\n- " + string.Join("\n- ", missing);
        return $"{state}\n\nConsole: {settings.BaseUrl}. Mode: {(enabled.On ? "writes enabled" : "read-only")}.";
    }
}
