# Switch Faceplate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `device_diagnostics_faq` answers with an MCP Apps view (device tiles, a switch faceplate, or a message) next to its markdown, in hosts that render views.

**Architecture:** The tool keeps its name and parameters and returns a `CallToolResult`: the markdown as text, a view model as `structuredContent`. One static resource at `ui://unifi-network/device-diagnostics.html` serves a single hand-written HTML file embedded in the assembly; it talks JSON-RPC over `postMessage` to the host. Flags, notes and "what is connected" are computed once on the server and feed both the text and the view model.

**Tech Stack:** .NET 10, `ModelContextProtocol.AspNetCore` 2.3.1 (no package added), xUnit with the existing `Harness`/`FakeUnifi`, plain HTML/CSS/JS with no build step.

**Spec:** `docs/superpowers/specs/2026-11-21-switch-faceplate-design.md` (approved by the owner 2026-11-21). Executors read it first.

## Global Constraints

- No new tool. Read-only the server lists 15 tools, 47 with writes. No NuGet package, no Node, no bundler.
- Tool `_meta`: `{"ui":{"resourceUri":"ui://unifi-network/device-diagnostics.html"}}` (nested form), on `device_diagnostics_faq` only. No `outputSchema`. Resource MIME `text/html;profile=mcp-app`, no `csp`, no `permissions`.
- Every value in the text, `structuredContent` and HTML is read by name with the `DiagnosticTools` readers (`V`, `Obj`, `Array`, `Bool`, `Dbl`, `Long`). No `JsonElement` from the console is serialised or passed through.
- The HTML loads nothing from the network: the file contains no `http://` or `https://` (so no SVG namespace either; draw with HTML and CSS), no `<link`, no `src=`, no `url(`, no `@import`.
- The view never polls: no `setInterval`, no fetch on hover, no `fetch(`/`XMLHttpRequest`. `setTimeout` is allowed only for Refresh's 10-second lock.
- All text reaches the DOM through `textContent`. No `innerHTML`, `outerHTML`, `insertAdjacentHTML` or `document.write`.
- Messages the view posts to the chat (`ui/message`) name ports by number and clients by MAC. Never a client's name or hostname. Owner-set device and port names are allowed.
- The tool description gains one clause about connected devices and says nothing about the view. It still starts with `[Composite] ` and stays under 4,096 characters.
- New reads happen only for a single matched device that has ports: `stat/sta` (internal, throttled) and the official `/v1/sites/{id}/devices` + `/v1/sites/{id}/devices/{deviceId}`. Each fails soft.
- Flags: `errors`, `flapping`, `half duplex` (faults) and `slow link` (slow). `slow link` is not raised when the port's known maximum is 100 Mb or less. No other flag. The summary (no device given) has no maximum to consult, so it flags as today; that difference is accepted by the spec.
- Fixtures use made-up names and addresses. The owner's real host never enters git (`deploy/compose.yaml` keeps `unifi.example.com`).
- After any change under `src/UnifiMcp/`, regenerate checksums from the repo root: `sha256sum -b $(git ls-files src/UnifiMcp | LC_ALL=C sort) > deploy/SHA256SUMS && sha256sum -c deploy/SHA256SUMS` (new files must be `git add`ed first).
- Work on branch `feat/switch-faceplate` (cut from `main`). Nothing is pushed and `<Version>` is not bumped; both are the owner's. Commits end with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.
- Test command: `dotnet test S:\Source\unifi-mcp\unifi-mcp.slnx` (expected: 0 failed). One test: add `--filter "FullyQualifiedName~<TestName>"`.

## Review Focus

1. **A console error on a call the view made itself** (Refresh or a tile click while the console is down or throttled) comes back as a normal result with `view: "message"`. The faceplate must stay, with the message in a status line; only a `device` result replaces the screen. (Task 5 host-page check H5; Task 3 test `Device_view_for_a_console_error_is_a_message`.)
2. **A device the official API does not list** (pending, or MAC written in another case): no "step failed" line, ports drawn as copper with no maximum, and a MAC differing only in case still matches. (Task 4 tests `Official_mac_match_ignores_case`, `Device_missing_from_the_official_list_is_not_an_error`.)
3. **Hostile names**: a client named `<img src=x onerror=alert(1)>` and a port named `a|b` render as text, do not break the markdown table, and never appear in a `ui/message`. (Task 4 test `Names_with_pipes_do_not_break_the_table`; Task 6 host-page check H9.)
4. **A 48+4 port switch at 320 px wide**: blocks of six wrap, nothing overflows sideways, every port is still a reachable button. (Task 6 host-page check H8.)
5. **A host result with no `structuredContent`**, or with `isError`: the view shows the result's text and no script error. (Task 5 host-page check H4.)

## File map

- Create `src/UnifiMcp/ViewResources.cs`: the view URI and MIME constants, and the registration of an embedded HTML file as an MCP resource. Reusable by later views.
- Create `src/UnifiMcp/Views/device-diagnostics.html`: the view (embedded).
- Create `src/UnifiMcp/DeviceViewModel.cs`: the records serialised into `structuredContent`.
- Create `src/UnifiMcp/DiagnosticTools.Devices.cs`: `partial class DiagnosticTools`; `device_diagnostics_faq`, `PortFlags`, `PortNote`, `Issues`, the connected-device join. Moved out of `DiagnosticTools.cs` so the private readers stay shared.
- Modify `src/UnifiMcp/DiagnosticTools.cs` (remove the moved members), `src/UnifiMcp/McpHost.cs` (`.WithResources`), `src/UnifiMcp/UnifiMcp.csproj` (embed the HTML), `deploy/SHA256SUMS`.
- Tests: create `tests/UnifiMcp.Tests/ViewTests.cs`; extend `tests/UnifiMcp.Tests/DiagnosticTests.cs` and `Harness.cs`.
- Create `tests/view-host/index.html` and `tests/view-host/samples.js`: development-only host emulator. Not embedded, not shipped, not under `src/`.
- Modify `README.md`, `CHANGELOG.md`, and the spec's status line.

---

### Task 1: View plumbing and a probe view

**Files:** Create `src/UnifiMcp/ViewResources.cs`, `src/UnifiMcp/Views/device-diagnostics.html`, `tests/UnifiMcp.Tests/ViewTests.cs`. Modify `src/UnifiMcp/UnifiMcp.csproj`, `src/UnifiMcp/McpHost.cs`, `src/UnifiMcp/DiagnosticTools.cs:269-276`, `tests/UnifiMcp.Tests/Harness.cs`, `deploy/SHA256SUMS`.

**Interfaces — Produces:**
- `public static class ViewResources` with `public const string DeviceDiagnosticsUri = "ui://unifi-network/device-diagnostics.html"`, `public const string MimeType = "text/html;profile=mcp-app"`, `internal static string Html(string logicalName)` (reads an embedded resource as UTF-8; throws `InvalidOperationException` naming it if missing), `internal static McpServerResource Create(string uri, string name, string title, string logicalName)`, `internal static IEnumerable<McpServerResource> All()`.
- Embedded resource logical name `views/device-diagnostics.html`.
- `Harness.CallRawAsync(string tool, Dictionary<string, object?>? args = null) : Task<CallToolResult>`.
- `DeviceDiagnostics` returns `Task<CallToolResult>`; `structuredContent` is the placeholder `{"view":"probe"}` until Task 3.

- [ ] **Step 1: Branch and commit the approved documents**

```powershell
git switch -c feat/switch-faceplate
```
The spec's line 3 already reads `Status: approved by the owner, 2026-11-21.` `git add docs/superpowers` and commit `docs: switch faceplate design and plan`.

- [ ] **Step 2: Write the failing tests** in `tests/UnifiMcp.Tests/ViewTests.cs`

```csharp
public class ViewTests
{
    private const string Uri = "ui://unifi-network/device-diagnostics.html";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_device_diagnostics_declares_a_view(bool writes)
    {
        await using var h = await Harness.StartAsync(writes: writes);
        var tools = (await h.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToList();
        Assert.Equal(writes ? 47 : 15, tools.Count);
        var withView = tools.Where(t => t.Meta?["ui"] is not null).ToList();
        Assert.Equal("device_diagnostics_faq", Assert.Single(withView).Name);
        Assert.Equal(Uri, withView[0].Meta!["ui"]!["resourceUri"]!.GetValue<string>());
        Assert.Null(withView[0].Meta!["ui/resourceUri"]);
        Assert.Null(withView[0].OutputSchema);
    }

    [Fact]
    public async Task The_view_is_served_as_an_mcp_app()
    {
        await using var h = await Harness.StartAsync();
        var r = await h.Client.ReadResourceAsync(Uri);
        var html = Assert.IsType<TextResourceContents>(Assert.Single(r.Contents));
        Assert.Equal("text/html;profile=mcp-app", html.MimeType);
        Assert.Contains("ui/initialize", html.Text);
        Assert.Null(html.Meta?["ui"]);
        Assert.Empty(h.Unifi.Requests); // serving the view makes no console call
    }

    [Theory]
    [InlineData("http://")] [InlineData("https://")] [InlineData("<link")] [InlineData("src=")] [InlineData("url(")]
    [InlineData("@import")] [InlineData("innerHTML")] [InlineData("outerHTML")] [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")] [InlineData("setInterval")] [InlineData("fetch(")] [InlineData("XMLHttpRequest")]
    public void The_view_loads_nothing_and_writes_no_markup(string banned) =>
        Assert.DoesNotContain(banned, ViewResources.Html("views/device-diagnostics.html"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task The_tool_description_does_not_mention_the_view()
    {
        await using var h = await Harness.StartAsync();
        var d = (await h.Client.ListToolsAsync()).Single(t => t.Name == "device_diagnostics_faq").Description;
        Assert.Contains("what is connected", d);
        foreach (var word in new[] { "view", "interactive", "widget", "faceplate", "UI" }) Assert.DoesNotContain(word, d);
    }
}
```

- [ ] **Step 3: Run them.** `dotnet test S:\Source\unifi-mcp\unifi-mcp.slnx --filter "FullyQualifiedName~ViewTests"`. Expected: build fails, `ViewResources` does not exist.

- [ ] **Step 4: Implement.**
  - `UnifiMcp.csproj`: `<EmbeddedResource Include="Views\device-diagnostics.html" LogicalName="views/device-diagnostics.html" />`.
  - `ViewResources.Create` uses `McpServerResource.Create(Delegate, McpServerResourceCreateOptions)` with `UriTemplate = uri`, `Name`, `Title`, `MimeType`; the delegate returns a `TextResourceContents` carrying `Uri`, `MimeType` and the HTML, read once. `All()` yields the one view: name `device-diagnostics`, title `Device diagnostics view`.
  - `McpHost.cs`: add `.WithResources(ViewResources.All())` after `.WithTools(tools)`.
  - `DiagnosticTools.cs`: on `DeviceDiagnostics` add `[McpMeta("ui", JsonValue = "{\"resourceUri\":\"" + ViewResources.DeviceDiagnosticsUri + "\"}")]`; change the return type to `Task<CallToolResult>`, wrapping today's text as `new CallToolResult { Content = [new TextContentBlock { Text = text }], StructuredContent = JsonSerializer.SerializeToElement(new { view = "probe" }) }`. In the description, change "plus every active port with speed, duplex, PoE draw," to "plus every active port with what is connected to it, speed, duplex, PoE draw,".
  - `Harness.CallRawAsync`: `Client.CallToolAsync(tool, args ?? [])`.
  - The probe HTML (replaced in Task 5): on load post `{jsonrpc:"2.0", id:1, method:"ui/initialize", params:{protocolVersion:"2026-01-26", appInfo:{name:"unifi-device-diagnostics", version:"0"}, appCapabilities:{}}}` to `window.parent` with target `"*"`; on its response post the notification `ui/notifications/initialized`; accept messages only when `event.source === window.parent`; on `ui/notifications/tool-result` show, with `textContent`, three lines: `unifi-mcp view probe: rendered`, `text blocks: <count>`, `structuredContent.view: <value or "missing">`; then post `ui/notifications/size-changed` with the body's width and height.

- [ ] **Step 5: Run the whole suite.** Expected: 0 failed, including every existing `DiagnosticTests` and `ToolContractTests` case.

- [ ] **Step 6: Checksums and commit.** `git add` the new files, regenerate `deploy/SHA256SUMS` (Global Constraints), commit `feat(view): register the device diagnostics view with a probe page`.

### Task 2: Checkpoint — does Claude render a view from this server? (hard stop, owner)

No code. This is the spec's Verification item 1.

- [ ] **Step 1: Stop and ask the owner** for the go-ahead to deploy branch `feat/switch-faceplate` (the probe) to their server, or to deploy it themselves. Deploying is outward-facing: do not do it on the strength of the design approval. If Claude deploys through Fleet, the real public host is substituted in `compose.yaml` inside the deploy call only, never in git.
- [ ] **Step 2: The owner (or Claude with them) checks three things** after the deploy, by asking Claude to run `device_diagnostics_faq`:
  1. Claude on the web or desktop shows `unifi-mcp view probe: rendered` with `structuredContent.view: probe`.
  2. In that same chat, Claude can quote a device name from the answer, so the model received the markdown.
  3. In Claude Code, the tool result the model sees is the markdown table, not `{"view":"probe"}`.
- [ ] **Step 3: Act on the outcome.**
  - 1 fails (no view anywhere): **stop**. Report to the owner; build nothing further.
  - 2 or 3 fails (the model gets the JSON instead of the markdown): continue, but in Task 3 `ViewResult.Create` puts the view model in `Meta["unifi-network/view"]` and leaves `StructuredContent` unset; tests read it from there. The view already reads both places (Task 5).
  - All pass: continue as written.

### Task 3: View model for the summary and the message answers

**Files:** Create `src/UnifiMcp/DeviceViewModel.cs`, `src/UnifiMcp/DiagnosticTools.Devices.cs`. Modify `src/UnifiMcp/DiagnosticTools.cs` (remove `DeviceDiagnostics`, `PortFlags`, `Issues`, `Temperature`, `DeviceState`), `tests/UnifiMcp.Tests/DiagnosticTests.cs`, `deploy/SHA256SUMS`.

**Interfaces — Produces** (`DeviceViewModel.cs`, all `internal sealed record`, serialised with `JsonSerializerDefaults.Web` and `DefaultIgnoreCondition = WhenWritingNull`, so names are camelCase and nulls are absent):

```csharp
record DiagnosticsView(string View, string? SiteId, string BuiltAt, IReadOnlyList<DeviceTile>? Devices = null,
    DeviceDetail? Device = null, string? Message = null, IReadOnlyList<Candidate>? Candidates = null);
record DeviceTile(string Name, string Mac, string Model, string Type, string State, long? UptimeSeconds, double? CpuPct,
    double? MemoryPct, double? TemperatureC, string Firmware, string? UpdateTo, IReadOnlyList<string> Issues);
record Candidate(string Name, string Ip, string Mac);
record DeviceDetail(string Name, string Mac, string Model, string Type, string State, string Ip, string Firmware, string? UpdateTo,
    long? UptimeSeconds, double? CpuPct, double? MemoryPct, double? TemperatureC, bool Overheating, double? PoeUsedW,
    double? PoeBudgetW, UplinkFacts? Uplink, IReadOnlyList<PortView> Ports);
record UplinkFacts(string Type, string? DeviceName, string? RemotePort, long? SpeedMbps);
record PortView(int Index, string Name, string State, long? SpeedMbps, long? MaxSpeedMbps, string Connector, string? Duplex,
    double? PoeW, long RxErrors, long TxErrors, long Drops, long LinkDowns, string? Stp, IReadOnlyList<string> Flags,
    string? Note, IReadOnlyList<Connected> Connected, int ConnectedCount);
record Connected(string Kind, string Name, string Mac);
static class ViewResult { internal static CallToolResult Create(string text, DiagnosticsView view); }
```

- `View` is `devices`, `device` or `message`. `SiteId` is the `siteId` argument exactly as given (null when omitted). `BuiltAt` is UTC ISO 8601. `State` on a tile or device is `DeviceState(...)`'s text (`online`, `offline`, ...). `UpdateTo` is null unless `upgradable`; then `upgrade_to_firmware`, or `available` when that is empty. `TemperatureC` is the hottest of `temperatures[].value`, else `general_temperature`.
- `ViewResult.Create` caps the text with `ToolText.Cap` and sets `StructuredContent` (or `Meta`, per Task 2's outcome).
- In `DiagnosticTools.Devices.cs`: `private async Task<CallToolResult> RunView(string? siteId, CancellationToken ct, Func<string, StringBuilder, Task<DiagnosticsView>> steps)`; on `UnifiError` or `UnifiNotConfigured` the text is the exception message and the view is `message` with that sentence. `IsError` stays unset, as today.
- This task fills `devices` and `message`. For one matched device it returns `view: "device"` with the header facts and `Ports = []`; Task 4 fills the ports.

- [ ] **Step 1: Write the failing tests** in `DiagnosticTests.cs`. Add `"x_authkey":"authkey-must-not-leak"` to the `Switch` fixture and that value to `NoSecrets`. Add the helper and tests:

```csharp
private static async Task<JsonElement> ViewAsync(Harness h, Dictionary<string, object?>? args = null)
{
    var r = await h.CallRawAsync("device_diagnostics_faq", args);
    var view = r.StructuredContent!.Value;
    NoSecrets(view.GetRawText());
    return view;
}

[Fact]
public async Task Device_view_without_a_device_lists_tiles_offline_first()
{
    await using var h = await StartAsync();
    var v = await ViewAsync(h);
    Assert.Equal("devices", v.GetProperty("view").GetString());
    Assert.False(v.TryGetProperty("siteId", out _));
    var tiles = v.GetProperty("devices").EnumerateArray().ToList();
    Assert.Equal(["Garage AP", "Core Switch", "Gateway", "Hall AP"], tiles.Select(t => t.GetProperty("name").GetString()));
    Assert.Equal("offline", tiles[0].GetProperty("state").GetString());
    var sw = tiles[1];
    Assert.Equal("aa:00:00:00:00:03", sw.GetProperty("mac").GetString());
    Assert.Equal(172800, sw.GetProperty("uptimeSeconds").GetInt64());
    Assert.Equal(3.0, sw.GetProperty("cpuPct").GetDouble());
    Assert.Equal(55.0, sw.GetProperty("memoryPct").GetDouble());
    Assert.Equal(61, sw.GetProperty("temperatureC").GetDouble());
    Assert.Equal("7.4.12", sw.GetProperty("updateTo").GetString());
    Assert.StartsWith("port(s) 2", sw.GetProperty("issues")[0].GetString());
    Assert.Equal(45, tiles[2].GetProperty("temperatureC").GetDouble());
    Assert.False(tiles[3].TryGetProperty("updateTo", out _));
}

[Fact]
public async Task Device_view_keeps_the_site_argument_as_given()
{
    await using var h = await StartAsync();
    Assert.Equal("Default", (await ViewAsync(h, new() { ["siteId"] = "Default" })).GetProperty("siteId").GetString());
}

[Fact]
public async Task Device_view_for_no_match_or_several_is_a_message()
{
    await using var h = await StartAsync();
    var none = await ViewAsync(h, new() { ["device"] = "toaster" });
    Assert.Equal("message", none.GetProperty("view").GetString());
    Assert.Contains("No device matches 'toaster'", none.GetProperty("message").GetString());
    Assert.False(none.TryGetProperty("candidates", out _));
    var two = await ViewAsync(h, new() { ["device"] = "AP" });
    Assert.Equal("message", two.GetProperty("view").GetString());
    var c = two.GetProperty("candidates").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Hall AP");
    Assert.Equal("192.168.1.20", c.GetProperty("ip").GetString());
    Assert.Equal("aa:00:00:00:00:01", c.GetProperty("mac").GetString());
}

[Fact]
public async Task Device_view_for_a_console_error_is_a_message()
{
    await using var h = await StartAsync();
    h.Unifi.Route("GET", $"{Classic}/stat/device", "{}", status: 500);
    var r = await h.CallRawAsync("device_diagnostics_faq");
    Assert.NotEqual(true, r.IsError);
    var v = r.StructuredContent!.Value;
    Assert.Equal("message", v.GetProperty("view").GetString());
    Assert.Contains("500", v.GetProperty("message").GetString());
}

[Fact]
public async Task Device_view_for_one_device_has_its_header_facts()
{
    await using var h = await StartAsync();
    var d = (await ViewAsync(h, new() { ["device"] = "core" })).GetProperty("device");
    Assert.Equal("Core Switch", d.GetProperty("name").GetString());
    Assert.Equal("192.168.1.2", d.GetProperty("ip").GetString());
    Assert.Equal(21.5, d.GetProperty("poeUsedW").GetDouble());
    Assert.Equal(95, d.GetProperty("poeBudgetW").GetDouble());
    Assert.False(d.GetProperty("overheating").GetBoolean());
    var up = d.GetProperty("uplink");
    Assert.Equal("Gateway", up.GetProperty("deviceName").GetString());
    Assert.Equal("9", up.GetProperty("remotePort").GetString());
    Assert.Equal(10000, up.GetProperty("speedMbps").GetInt64());
}
```

- [ ] **Step 2: Run them.** `--filter "FullyQualifiedName~Device_view"`. Expected: FAIL (`view` is `probe`).
- [ ] **Step 3: Implement.** Move the device members into `DiagnosticTools.Devices.cs` unchanged, then make `DeviceDiagnostics` build the text exactly as today and return the view from the same pass through `RunView`. The message for several matches keeps today's text (sentence plus table); the view's `message` is the sentence alone.
- [ ] **Step 4: Run the whole suite.** Expected: 0 failed; the three existing `Device_diagnostics_*` tests pass untouched.
- [ ] **Step 5: Checksums and commit** `feat(view): view model for the device summary and messages`.

### Task 4: Ports — what is connected, maximum speed, flags and notes

**Files:** Modify `src/UnifiMcp/DiagnosticTools.Devices.cs`, `tests/UnifiMcp.Tests/DiagnosticTests.cs`, `deploy/SHA256SUMS`.

**Interfaces — Consumes:** Task 3's records and `RunView`. **Produces** (in `DiagnosticTools.Devices.cs`):
- `internal static List<string> PortFlags(JsonElement port, long? maxSpeedMbps)`: today's four rules in today's order, except `slow link` is skipped when `maxSpeedMbps is <= 100`. `Issues` calls it with `null`.
- `internal static string SpeedLabel(long? mbps)`: `?` for null, `"{n} Mb"` below 1000, else `"{n/1000} GbE"` in invariant culture (`1 GbE`, `2.5 GbE`, `10 GbE`).
- `internal static string? PortNote(IReadOnlyList<string> flags, long? speed, long? max, long rxErrors, long txErrors, long linkDowns)`.
- Steps named `Connected clients` and `Port capabilities` (the names appear in "**… step failed:**" lines).

Rules the tests do not fully determine:
- **Connected, per port index.** UniFi devices first: every other device in the `stat/device` list whose `uplink.uplink_mac` equals this device's MAC (ignoring case), or, when it has no `uplink_mac`, whose `uplink.uplink_device_name` equals this device's name; port = `uplink.uplink_remote_port`; kind `device`, name = its `name`. Then clients from `stat/sta` with `is_wired` true and `sw_mac` equal to this MAC (ignoring case), port = `sw_port`; kind `client`, name = `Name(c)`. `Connected` holds the first five in that order (clients by name), `ConnectedCount` the total.
- **Port capabilities.** `console.ResolveSiteAsync(siteId, ct)`, then `GetAllAsync("/v1/sites/{id}/devices")`; the entry whose `macAddress` equals this MAC ignoring case; then `GetJsonAsync("/v1/sites/{id}/devices/{deviceId}")` and `interfaces.ports[]` keyed by `idx`: `maxSpeedMbps`, and `connector` only when it is one of `RJ45`, `SFP`, `SFPPLUS`, `SFP28`, `QSFP28`. No matching entry is not a failure. Unknown connector is `RJ45`; unknown maximum is null.
- Both steps are skipped when `port_table` is empty.
- **Port state:** `up`, else `down` when `enable`, else `disabled`. `Duplex` is `full`/`half` when up, else null. `PoeW` only when `poe_enable`. `Drops` = rx + tx dropped. The view model lists every port; the text table keeps today's "active" filter.
- **Text table columns:** `Port, Name, Connected, Link, Speed (Mbps), Max (Mbps), Duplex, PoE W, Rx errors, Tx errors, Drops, Link downs, STP, Flags`. The Connected cell is the names joined with `, `, then ` +N more` past five.
- **Note:**

```csharp
if (flags.Count == 0) return null;
var parts = new List<string>();
if (flags.Contains("half duplex") || flags.Contains("slow link"))
    parts.Add($"linked at {SpeedLabel(speed)}{(flags.Contains("half duplex") ? " half duplex" : "")}"
              + (max > speed ? $" on a port that can do {SpeedLabel(max)}" : ""));
if (flags.Contains("errors"))
    parts.Add(rxErrors > 0 && txErrors > 0 ? "receive and send errors" : rxErrors > 0 ? "receive errors" : "send errors");
if (flags.Contains("flapping")) parts.Add($"link dropped {linkDowns} times since the device restarted");
var cause = flags.Contains("errors") || flags.Contains("half duplex") ? "suspect the cable or the device's network card"
    : flags.Contains("flapping") ? "check the cable and the device's power"
    : "the device or its cable may only support this speed";
var s = string.Join("; ", parts);
return char.ToUpperInvariant(s[0]) + s[1..] + ": " + cause + ".";
```

- [ ] **Step 1: Extend the fixtures** in `DiagnosticTests.cs`:
  - `Ap.uplink`: add `"uplink_mac":"aa:00:00:00:00:03"` and change `uplink_remote_port` to `1`.
  - `Switch.port_table`: append
    `{"port_idx":4,"name":"Printer","up":true,"enable":true,"speed":100,"full_duplex":true,"rx_errors":0,"tx_errors":0,"link_down_count":0}`,
    `{"port_idx":5,"name":"SFP 1","up":true,"enable":true,"speed":10000,"full_duplex":true,"rx_errors":0,"tx_errors":0,"link_down_count":0}`,
    `{"port_idx":6,"name":"a|b","up":false,"enable":false,"rx_errors":0,"tx_errors":0,"link_down_count":0}`.
  - New constants: `OfficialId = "33333333-3333-3333-3333-333333333333"`; `OfficialSwitch` = `{"id":"<OfficialId>","macAddress":"AA:00:00:00:00:03","name":"Core Switch"}`; `OfficialDetail` = the same plus `"adoptionKey":"official-must-not-leak"` and `"interfaces":{"ports":[…]}` with idx 1, 2, 3, 6 `RJ45`/1000, idx 4 `RJ45`/100, idx 5 `SFPPLUS`/10000 (each with `"state":"UP"`). Add `official-must-not-leak` to `NoSecrets`.
  - `Seed`: route `GET /v1/sites/{Harness.SiteId}/devices` to `FakeUnifi.Page(OfficialSwitch)` and `GET /v1/sites/{Harness.SiteId}/devices/{OfficialId}` to `OfficialDetail`.

- [ ] **Step 2: Write the failing tests**

```csharp
private static async Task<List<JsonElement>> PortsAsync(Harness h) =>
    (await ViewAsync(h, new() { ["device"] = "core" })).GetProperty("device").GetProperty("ports").EnumerateArray().ToList();
private static string[] Flags(JsonElement port) => port.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();
private const string NasNote = "Linked at 100 Mb half duplex on a port that can do 1 GbE; receive errors; link dropped 14 times since the device restarted: suspect the cable or the device's network card.";

[Fact]
public async Task Device_view_for_a_switch_has_every_port_with_flags_notes_and_what_is_connected()
{
    await using var h = await StartAsync();
    var p = await PortsAsync(h);
    Assert.Equal([1, 2, 3, 4, 5, 6], p.Select(x => x.GetProperty("index").GetInt32()));
    Assert.Equal(["up", "up", "down", "up", "up", "disabled"], p.Select(x => x.GetProperty("state").GetString()));
    Assert.Equal("device", p[0].GetProperty("connected")[0].GetProperty("kind").GetString());
    Assert.Equal("Hall AP", p[0].GetProperty("connected")[0].GetProperty("name").GetString());
    Assert.Equal(6.65, p[0].GetProperty("poeW").GetDouble());
    Assert.Empty(Flags(p[0]));
    Assert.False(p[0].TryGetProperty("note", out _));
    Assert.Equal(["errors", "flapping", "half duplex", "slow link"], Flags(p[1]));
    Assert.Equal(NasNote, p[1].GetProperty("note").GetString());
    Assert.Equal(1000, p[1].GetProperty("maxSpeedMbps").GetInt64());
    Assert.Equal("half", p[1].GetProperty("duplex").GetString());
    Assert.Equal(1532, p[1].GetProperty("rxErrors").GetInt64());
    Assert.Equal(12, p[1].GetProperty("drops").GetInt64());
    Assert.Equal(14, p[1].GetProperty("linkDowns").GetInt64());
    Assert.Equal("client", p[1].GetProperty("connected")[0].GetProperty("kind").GetString());
    Assert.Equal("cc:00:00:00:00:01", p[1].GetProperty("connected")[0].GetProperty("mac").GetString());
    Assert.Equal(1, p[1].GetProperty("connectedCount").GetInt32());
    Assert.Empty(Flags(p[3]));                       // 100 Mb on a port whose maximum is 100 Mb is full speed
    Assert.Equal("SFPPLUS", p[4].GetProperty("connector").GetString());
    Assert.False(p[1].TryGetProperty("poeW", out _)); // PoE is off on that port
}

[Fact]
public async Task The_text_answer_gains_connected_and_maximum_columns()
{
    await using var h = await StartAsync();
    var (text, _) = await h.CallAsync("device_diagnostics_faq", new() { ["device"] = "core" });
    Assert.Contains("| Port | Name | Connected | Link | Speed (Mbps) | Max (Mbps) |", text);
    Assert.Contains("| 2 | NAS | nas | up | 100 | 1000 | half |", text);
    Assert.Contains("| 1 | Port 1 | Hall AP |", text);
    Assert.Contains("Down with nothing to report: 3, 6.", text);
    Assert.DoesNotContain("step failed", text);
    NoSecrets(text);
}

[Fact]
public async Task Device_view_survives_a_failing_clients_step()
{
    await using var h = await StartAsync();
    h.Unifi.Route("GET", $"{Classic}/stat/sta", "{}", status: 500);
    var r = await h.CallRawAsync("device_diagnostics_faq", new() { ["device"] = "core" });
    Assert.Contains("**Connected clients step failed:**", ((TextContentBlock)r.Content[0]).Text);
    var p = r.StructuredContent!.Value.GetProperty("device").GetProperty("ports").EnumerateArray().ToList();
    Assert.Equal(0, p[1].GetProperty("connectedCount").GetInt32());
    Assert.Equal(1, p[0].GetProperty("connectedCount").GetInt32()); // the AP comes from stat/device
    Assert.Equal(1000, p[1].GetProperty("maxSpeedMbps").GetInt64());
}

[Fact]
public async Task Device_view_survives_a_failing_official_step()
{
    await using var h = await StartAsync();
    h.Unifi.Route("GET", $"/v1/sites/{Harness.SiteId}/devices", "{}", status: 500);
    var r = await h.CallRawAsync("device_diagnostics_faq", new() { ["device"] = "core" });
    Assert.Contains("**Port capabilities step failed:**", ((TextContentBlock)r.Content[0]).Text);
    var p = r.StructuredContent!.Value.GetProperty("device").GetProperty("ports").EnumerateArray().ToList();
    Assert.All(p, x => Assert.Equal("RJ45", x.GetProperty("connector").GetString()));
    Assert.All(p, x => Assert.False(x.TryGetProperty("maxSpeedMbps", out _)));
    Assert.Equal(["slow link"], Flags(p[3]));
    Assert.Equal("Linked at 100 Mb half duplex; receive errors; link dropped 14 times since the device restarted: suspect the cable or the device's network card.", p[1].GetProperty("note").GetString());
    Assert.Equal("nas", p[1].GetProperty("connected")[0].GetProperty("name").GetString());
}

[Fact]
public async Task Device_missing_from_the_official_list_is_not_an_error()
{
    await using var h = await StartAsync();
    h.Unifi.Route("GET", $"/v1/sites/{Harness.SiteId}/devices", FakeUnifi.Page());
    var (text, _) = await h.CallAsync("device_diagnostics_faq", new() { ["device"] = "core" });
    Assert.DoesNotContain("step failed", text);
    Assert.Contains("| 2 | NAS | nas | up | 100 |  | half |", text);
}

[Fact]
public async Task Official_mac_match_ignores_case() // the fixture's official MAC is upper case, the console's lower
{
    await using var h = await StartAsync();
    Assert.Equal(10000, (await PortsAsync(h))[4].GetProperty("maxSpeedMbps").GetInt64());
}

[Fact]
public async Task A_port_lists_five_connected_and_counts_the_rest()
{
    await using var h = await StartAsync();
    h.Unifi.Route("GET", $"{Classic}/stat/sta", Ok(Enumerable.Range(1, 7).Select(i =>
        $$"""{"mac":"cc:00:00:00:01:0{{i}}","name":"cam {{i}}","is_wired":true,"sw_mac":"AA:00:00:00:00:03","sw_port":4}""").ToArray()));
    var r = await h.CallRawAsync("device_diagnostics_faq", new() { ["device"] = "core" });
    var port4 = r.StructuredContent!.Value.GetProperty("device").GetProperty("ports")[3];
    Assert.Equal(5, port4.GetProperty("connected").GetArrayLength());
    Assert.Equal(7, port4.GetProperty("connectedCount").GetInt32());
    Assert.Contains("cam 5 +2 more", ((TextContentBlock)r.Content[0]).Text);
}

[Fact]
public async Task Names_with_pipes_do_not_break_the_table()
{
    await using var h = await StartAsync();
    h.Unifi.Route("GET", $"{Classic}/stat/sta", Ok("""{"mac":"cc:00:00:00:02:01","name":"<img src=x onerror=alert(1)>|x","is_wired":true,"sw_mac":"aa:00:00:00:00:03","sw_port":4}"""));
    var (text, _) = await h.CallAsync("device_diagnostics_faq", new() { ["device"] = "core" });
    Assert.Contains(@"<img src=x onerror=alert(1)>\|x", text);
}

[Fact]
public async Task A_device_without_ports_reads_nothing_more()
{
    await using var h = await StartAsync();
    var v = await ViewAsync(h, new() { ["device"] = "192.168.1.1" });
    Assert.Empty(v.GetProperty("device").GetProperty("ports").EnumerateArray());
    Assert.DoesNotContain(h.Unifi.Requests, r => r.Path.Contains("/stat/sta") || r.Path.Contains("/devices"));
}

[Theory]
[InlineData(100L, "100 Mb")] [InlineData(1000L, "1 GbE")] [InlineData(2500L, "2.5 GbE")] [InlineData(10000L, "10 GbE")]
public void Speeds_read_the_way_people_say_them(long mbps, string label) => Assert.Equal(label, DiagnosticTools.SpeedLabel(mbps));

[Fact]
public void A_slow_only_port_gets_a_note_about_the_device()
{
    Assert.Equal("Linked at 100 Mb on a port that can do 1 GbE: the device or its cable may only support this speed.",
        DiagnosticTools.PortNote(["slow link"], 100, 1000, 0, 0, 0));
    Assert.Equal("Link dropped 6 times since the device restarted: check the cable and the device's power.",
        DiagnosticTools.PortNote(["flapping"], 1000, 1000, 0, 0, 6));
    Assert.Null(DiagnosticTools.PortNote([], 1000, 1000, 0, 0, 0));
}
```

- [ ] **Step 3: Run them.** `--filter "FullyQualifiedName~DiagnosticTests"`. Expected: the new tests FAIL (`ports` is empty; members missing).
- [ ] **Step 4: Implement** per the Interfaces block and rules above.
- [ ] **Step 5: Run the whole suite.** Expected: 0 failed. `Client_diagnostics_for_a_wired_client_*` still passes (the `Ap` fixture change does not touch it).
- [ ] **Step 6: Checksums and commit** `feat(view): ports with what is connected, maximum speed, flags and notes`.

### Task 5: The view — protocol client, theme, tiles and messages, and the development host page

**Files:** Rewrite `src/UnifiMcp/Views/device-diagnostics.html`. Create `tests/view-host/index.html`, `tests/view-host/samples.js`. Modify `deploy/SHA256SUMS`.

**Interfaces — Consumes:** the camelCase view model from Tasks 3 and 4. **Produces**, inside the HTML:
- A script section between the comments `/* mcp-app client: reusable, start */` and `/* mcp-app client: reusable, end */` that knows nothing about UniFi and defines `createApp({ name, version })` returning:
  `ready` (promise of the `ui/initialize` result), `onResult(fn)` (each `ui/notifications/tool-result` params), `onInput(fn)` (`ui/notifications/tool-input` arguments), `onContext(fn)` (the initial `hostContext`, then each `ui/notifications/host-context-changed` merged over it), `callTool(name, args)` (request `tools/call`, resolves with the `CallToolResult`, rejects on a JSON-RPC error), `sendMessage(text)` (request `ui/message`, `{role:"user", content:{type:"text", text}}`), `updateContext(text)` (request `ui/update-model-context`, `{content:[{type:"text", text}]}`; a rejection is swallowed), and automatic `ui/notifications/size-changed` `{width, height}` from a `ResizeObserver` on `document.body`.
- `modelOf(result)`: `result.structuredContent ?? result._meta?.["unifi-network/view"]`, accepted only when its `view` is `devices`, `device` or `message`; otherwise null.
- `show(model)` and a single `state` object (`{ model, devicesModel, selectedPort, status }`) that Task 6 extends.

Decisions:
- **Handshake:** as the probe did (Task 1 Step 4), `protocolVersion` `"2026-01-26"`, `appInfo` `{name:"unifi-device-diagnostics", version:"1"}`. Messages are accepted only when `event.source === window.parent`; posts use target `"*"`.
- **No model:** show the result's text blocks in a `<pre>` (with `textContent`); before any result, show "Waiting for the answer…".
- **Theme:** `data-theme` on `<html>` is `hostContext.theme`, else `prefers-color-scheme`. Colours are CSS variables. Dark: ground `#0F1318`, surface `#171C23`, border `#2A3340`, text `#E6EAF0`, muted `#9AA6B5`, blue `#5AA9FF`, orange `#FFA24A`, pink-red `#FF7A88`. Light: ground `#F6F8FA`, surface `#FFFFFF`, border `#D5DCE5`, text `#1B2430`, muted `#5B6877`, blue `#1F6FD6`, orange `#B25E00`, pink-red `#C8324A`. Of the host's `styles.variables`, only `--font-sans` and `--font-mono` are applied; `styles.css.fonts` is ignored (it would load from the network). Font stack: `var(--font-sans, system-ui, -apple-system, "Segoe UI", sans-serif)`.
- **Devices state:** a grid of tile buttons in the order given: name, model, state, uptime (`2 d 0 h` style), CPU and memory, temperature, an "update available" mark when `updateTo` is present, one chip per issue. Offline tiles carry the pink-red state text.
- **Tile click:** `callTool("device_diagnostics_faq", { device: tile.mac, siteId })` (`siteId` only when the model has one). While it runs the tile is disabled.
- **Results of the view's own calls** (Review Focus 1): a `device` model replaces the screen and keeps `devicesModel` for Back; anything else, and any rejection, leaves the screen as it is and puts the sentence (the model's `message`, or "Could not refresh: <error message>") in a status line.
- **Back** appears on a device screen only when `devicesModel` exists; it re-shows that model without a call.
- **Message state:** the sentence, then candidate tiles (name, IP, MAC) that behave like device tiles.
- **Host page** `tests/view-host/index.html` (open the file directly in a browser; if the browser refuses `file:` frames, serve the repo root with any static server, not committed): frames `../../src/UnifiMcp/Views/device-diagnostics.html`; answers `ui/initialize` with `hostContext {theme, styles:{variables:{"--font-sans": "Georgia, serif"}}}`; sends `tool-input` then `tool-result` for the chosen sample; answers `tools/call` from `samples.js` by `arguments.device` MAC; applies `size-changed` height to the frame; logs every message from the view (method and params) in a visible list. Controls: sample picker, theme (sends `host-context-changed`), frame width 320/480/760, and three switches: "refuse tools/call" (JSON-RPC error `-32000`), "console is down" (answers a `message` model), "text-only result" (no `structuredContent`).
- **`samples.js`** sets `window.SAMPLES`: `devices` (four devices, one offline, one with an update, one with issues), `message` with two candidates, `messageOnly`, and device models keyed by MAC: `core` (24 copper + 2 `SFPPLUS`; port 2 all four flags with a note, 1 client; port 5 `slow link` only; port 11 up at 100 with `maxSpeedMbps` 100 and no flag; port 15 `flapping`; port 23 `connectedCount` 7 with five listed; several ports with `poeW`; one disabled), `big` (48 copper + 4 `SFP28`), `flex` (5 copper), `gateway` (no ports), `hostile` (a connected client named `<img src=x onerror=alert(1)>`, a port named `"a|b"`). Field names exactly as in Task 3's records.

- [ ] **Step 1: Write the host page and samples.**
- [ ] **Step 2: Write the view** up to and including the devices and message states; for a `device` model show the header line only (Task 6 draws the rest).
- [ ] **Step 3: Run `ViewTests`.** Expected: PASS (banned-string and resource tests still hold for the real file).
- [ ] **Step 4: Check in a browser** with the host page. Each must hold in both themes:
  - H1 `devices` sample: four tiles, offline first, update mark and issue chips shown; the log shows `ui/initialize`, `ui/notifications/initialized`, then `size-changed`, and the frame grows to fit.
  - H2 clicking the Core Switch tile logs one `tools/call` with `{device: "<its MAC>"}` and no `ui/message`; the header line of the device appears; Back returns to the tiles with no new call.
  - H3 `message` sample: sentence plus two candidate tiles; a candidate click behaves like H2. `messageOnly`: the sentence alone.
  - H4 "text-only result": the text shows in place of the view; the browser console has no error.
  - H5 with a device open, "console is down" then a tile click or Back-and-click: the screen stays, the status line shows the sentence. Same with "refuse tools/call".
  - H6 switching theme re-colours without a reload; the font changes to the host's `--font-sans`.
- [ ] **Step 5: Checksums and commit** `feat(view): device tiles, messages and the development host page`.

### Task 6: The view — faceplate, port panel and the buttons that talk to Claude

**Files:** Modify `src/UnifiMcp/Views/device-diagnostics.html`, `deploy/SHA256SUMS`.

**Interfaces — Consumes:** `createApp`, `show`, `state` and the host page from Task 5.

Decisions:
- **Header:** name · model · state · `PoE <used> of <budget> W` (when a budget exists) · `up <uptime>`, and a Refresh button.
- **Layout:** copper ports (`connector === "RJ45"`) in blocks of six columns, two rows per block, odd index on top; blocks wrap. Five copper ports or fewer: one row. Other connectors: a separate group after the copper, labelled with the port's name. No ports: header only, no legend, no panel.
- **Port button:** shows the index. Class by precedence: disabled (dimmed) → fault (pink-red; any of `errors`, `flapping`, `half duplex`) → slow (orange; `slow link` only) → up (blue) → no link (outline). Fault ports carry a `!` mark and slow ports a `~` mark, so colour is not the only signal. A bar under the port when `poeW > 0`. `aria-label`: `Port <n>, <name>, <state>` plus `, <flags joined>` when flagged. `aria-pressed` on the selected one. A legend line names the five looks.
- **Selection on load and after Refresh:** the previously selected index if it still exists; else the first fault port; else the first slow port; else none, and the panel reads "Pick a port to see its details."
- **Panel:** `Port <n> · <name>`; Link (`<SpeedLabel>, <duplex> duplex` plus `(port can do <SpeedLabel of max>)` when a maximum is known, or `no link` / `disabled`); Connected (names, then `+N more`; "nothing seen" when empty); PoE (`<w> W` or `off`); `Errors <rx> received, <tx> sent`, `Drops <n>`, `Link downs <n>`; the line `Counters run since the device restarted, <uptime> ago.`; the note when present. Numbers use `toLocaleString()`. The speed label follows Task 4's `SpeedLabel` rule.
- **Model context:** on opening a device and on each selection, `updateContext` with one line: `The user is looking at <device name> (<device MAC>) in the device diagnostics view` + `, port <n> ("<port name>"; <state>[; <flags>]).` or `.` when no port is selected.
- **Refresh:** `callTool` with the same `device` MAC and `siteId`; disabled while running and for 10 seconds after, by one `setTimeout`.
- **Ask Claude why** (only on a port with at least one flag) sends exactly:
  `Why is port <n> ("<port name>") on <device name> (<device MAC>) flagged <flags joined with ", ">? It is linked at <speed> Mb <duplex> duplex[, port maximum <max> Mb], with <rx> receive errors, <tx> send errors, <drops> drops and <linkDowns> link downs since the device restarted. What is the likely cause?`
- **Trace its path** (only when `connectedCount === 1`) sends exactly: `Trace the network path of the client with MAC <mac>.`
- A rejected `sendMessage` puts "Claude did not take the message." in the status line.

- [ ] **Step 1: Implement** the device state per the decisions above.
- [ ] **Step 2: Run `ViewTests`.** Expected: PASS.
- [ ] **Step 3: Check in a browser** with the host page, both themes:
  - H7 `core`: two rows of 12 in two blocks, SFP group apart; port 2 pink-red with `!` and selected on load; port 5 orange with `~`; port 11 blue; the disabled port dimmed; PoE bars where `poeW > 0`. The panel for port 2 matches the spec's sketch, with "since the device restarted" and the note. The log shows one `ui/update-model-context` on open and one per selection. Tab reaches every port; Enter and Space select.
  - H8 `big` at 320 px: blocks wrap, no sideways scroll, all 52 ports clickable. `flex`: one row of five. `gateway`: header only.
  - H9 `hostile`: the client's name shows as literal text in the panel; "Trace its path" logs a `ui/message` containing the MAC and not the name; "Ask Claude why" on a flagged port logs the exact sentence above with no client name in it.
  - H10 Refresh logs one `tools/call`, stays disabled for 10 seconds, and keeps the selected port. Hovering ports logs nothing.
  - H11 port 23: five names then `+2 more`; no "Trace its path". An unflagged port: no "Ask Claude why", no note.
- [ ] **Step 4: Checksums and commit** `feat(view): the switch faceplate, port details and questions for Claude`.

### Task 7: Documentation and the owner's checks

**Files:** Modify `README.md`, `CHANGELOG.md`.

- [ ] **Step 1: README.** Under "## Tools", after the diagnostics bullet, add a bullet: `device_diagnostics_faq` also carries an interactive view (device tiles, a switch faceplate with per-port details) for hosts that render MCP Apps; other hosts get the same answer as text; the view makes no writes and loads nothing from the network. Leave "15 tools; with writes on, 47" as it is. Under "## Develop", one sentence: open `tests/view-host/index.html` to exercise the view without a host.
- [ ] **Step 2: CHANGELOG.** Replace "Nothing yet." under "## Unreleased" with an "### Added" list: the view on `device_diagnostics_faq`; the "Connected" and "Max (Mbps)" columns; and a "### Changed" line: `slow link` is no longer raised on a port whose maximum speed is 100 Mb.
- [ ] **Step 3: Run the whole suite.** Expected: 0 failed. Commit `docs: the device diagnostics view`.
- [ ] **Step 4: Stop for the owner's look** (spec Verification item 3): the owner opens the host page, or is sent screenshots of H1, H7 and H8 in both themes, and confirms the look before any deploy.
- [ ] **Step 5: Deploy and check in Claude** (spec Verification item 4), with the owner's go-ahead as in Task 2: tiles, drill-in, port selection, both buttons and Refresh against the real network. Merging, the version bump and the tag are the owner's.
