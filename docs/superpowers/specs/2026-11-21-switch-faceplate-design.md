# Switch faceplate: an interactive view for device_diagnostics_faq

Status: approved by the owner, 2026-11-21. Built on `feat/switch-faceplate`.

Outcome of Verification item 1 (2026-11-21): Claude renders the view, but hands the model `structuredContent` in place
of the markdown. So the fallback below is what was built: the view's data travels in the result's
`_meta["unifi-network/view"]`, and wherever this document says `structuredContent`, read that key.

## Intent

`device_diagnostics_faq` answers "which port is flapping" with a markdown table. The owner wants hosts that can render
MCP Apps (the `io.modelcontextprotocol/ui` extension, spec 2026-01-26) to show the same answer as a picture of the
switch: every port coloured by its state, click one to see what is wrong and what is plugged into it, and hand the
question back to Claude with one button.

This is the first interactive view in unifi-mcp. It also sets the pattern the later ones (change preview, path trace,
and the rest of the mockups) will follow, so the plumbing should be reusable and small.

## Decided by the owner

- **The view attaches to the existing `device_diagnostics_faq` tool.** No new tool. The tool list stays at 15 read-only
  and 47 with writes.
- **Switch faceplate first**, ahead of the other seven mocked-up views.

## Goals

1. In a host that renders MCP Apps, a `device_diagnostics_faq` call shows a view instead of only a table:
   - no `device`: one tile per device;
   - one device matched: its faceplate;
   - no match, several matches, or an error: the message, with the candidates as tiles when there are any.
2. Clicking a device tile opens that device's faceplate without a new chat turn.
3. Clicking a port shows its link, what is connected, PoE draw, error and drop counters, link-down count and a
   plain-language note.
4. "Ask Claude why" and "Trace its path" put a question into the chat for Claude to answer with the existing tools.
5. Hosts without MCP Apps support get a text answer that is at least as good as today's.

## Non-goals

- Any write from the view. PoE power-cycle, port enable/disable and renaming wait for the change-preview work.
- A physically faithful drawing of each switch model. The layout is generic (see Faceplate layout).
- Radios, storage and temperature charts in the view. The text answer keeps them.
- Live updating. The view refreshes only when the user presses Refresh.
- A JavaScript build step, package manager or test runner in this repository.
- Views for any other tool.

## Anti-goals (failure even if it works)

- A raw console object, or any field not named in this document, reaching `structuredContent` or the HTML. The class
  rule in `DiagnosticTools` stands: answers are built from named fields because the raw objects carry secrets.
- The view loading anything from the network (fonts, scripts, images) or declaring any CSP domain.
- The view calling the console more often than a person clicking would: no polling, no timers, no fetch on hover.
- A string a network device chose for itself (a client hostname or name) appearing in a message the view posts into the
  chat as the user. Those messages name ports by number and clients by MAC.
- The text answer losing anything it has today.
- The model being steered to call `device_diagnostics_faq` more often because it now has a view.

## Constraints

- **SDK:** `ModelContextProtocol.AspNetCore` 2.3.1. It has what this needs: `[McpMeta(name, JsonValue = ...)]` for tool
  `_meta`, `CallToolResult.StructuredContent`, `WithResources(...)` and `McpServerResource.Create(...)` with
  `McpServerResourceCreateOptions.MimeType`. No package is added.
- **Stateless transport** (`WithHttpTransport(o => o.Stateless = true)`): the server cannot tailor the tool list to a
  client's capabilities, so the view metadata is always declared. Hosts that do not know it ignore it.
- **Console throttle:** internal reads go one at a time, are cached for 10 seconds, and are limited to 30 a minute after
  a burst of 20. The view's own tool calls go through the same throttle.
- **Public repository:** fixtures and examples use made-up names and addresses, never the owner's.
- **Tool contract tests** (`ToolContractTests`) must keep passing: the description still starts with `[Composite] ` and
  stays under 4,096 characters.

## Approaches

**Where the view lives**

- **A. On `device_diagnostics_faq` (chosen by the owner).** The tool already reads `stat/device`, which carries
  `port_table`. A host shows a tool's view on every call, so the view handles all three kinds of answer. Costs: more
  than one view state to build, and every diagnostics answer carries a view.
- **B. A new `switch_ports` tool.** The view is only ever the faceplate. Costs: a 16th tool that overlaps the existing
  one, the model has to choose between them, and the README's tool counts change. Rejected: the project keeps its tool
  list short on purpose.
- **C. An app-only helper tool (`visibility: ["app"]`) for the drill-in.** Not needed: the view can call
  `device_diagnostics_faq` itself, and the cache makes a repeat cheap.

**How the view is built** (the author's call; the owner did not object)

- **One hand-written HTML file, embedded in the assembly (chosen).** Inline CSS and JS, system fonts, and a small
  hand-written JSON-RPC-over-`postMessage` client. No toolchain; the file is reviewable as it ships.
- **The `@modelcontextprotocol/ext-apps` SDK, bundled.** Less protocol code to write, but it brings Node, a bundler and
  a build step into a .NET-only repository for a handful of messages. Rejected for now; revisit if a third view makes
  the hand-written client a burden.

**Where the view's data travels** (the author's call)

- **`structuredContent` on the tool result (chosen).** It is what the MCP Apps spec prescribes: `content` is the text
  for the model and text-only hosts, `structuredContent` is for the view and is not added to model context.
- **A namespaced key in the result's `_meta`.** The fallback only if a host the owner uses turns out to give the model
  `structuredContent` in place of the markdown (see Verification, item 1).

## Design

### Server

**Tool.** `device_diagnostics_faq` keeps its name, parameters and annotations. It gains:

- `_meta.ui.resourceUri = "ui://unifi-network/device-diagnostics.html"` (the nested form; the flat
  `ui/resourceUri` key is deprecated). Visibility stays at the default, so both the model and the view may call it.
- A `CallToolResult` return with the markdown as its one text block and the view model as `structuredContent`. No
  `outputSchema` is declared.
- One clause in the description saying each port lists what is connected to it. Nothing about the view: the description
  is for the model, and the view must not make the tool more attractive to call.

**Resource.** One static resource at that URI, MIME type `text/html;profile=mcp-app`, served from an embedded file
(`src/UnifiMcp/Views/device-diagnostics.html`). It makes no console call. Its `_meta.ui` declares no `csp` and no
`permissions`.

**New reads, single-device answers only.** Each is a step that may fail without stopping the rest, like the existing
`Step` helper:

- `stat/sta` (internal, throttled, already allow-listed): wired clients with `sw_mac` equal to the device's MAC, grouped
  by `sw_port`. This gives the names for "what is connected".
- The official API's device detail (`/v1/sites/{id}/devices/{deviceId}`, found by MAC in `/v1/sites/{id}/devices`):
  `interfaces.ports[].maxSpeedMbps` and `connector`, matched to `port_table` by port index. This gives "the port can do
  1 GbE" and tells copper ports from SFP cages.

UniFi devices on a port (an AP, another switch) need no extra read: every device in `stat/device` names its parent in
`uplink` (`uplink_device_name`, `uplink_remote_port`, and the parent's MAC), so the tool can label the parent's port
from the list it already has.

If the clients step fails, ports show without names. If the official step fails, every port is drawn as copper, no
maximum is shown, and `slow link` is raised as it is today.

**The view model.** One object with a `view` discriminator. Field names are the builder's to finalise; the content is
fixed:

- Common: `view` (`devices`, `device` or `message`), the `siteId` argument as it was given, and the time it was built.
- `devices`: per device, the columns of today's table as typed values (name, MAC, model, type, state, uptime seconds,
  CPU %, memory %, hottest temperature, firmware, pending update, issue list).
- `device`: the header facts the text prints (name, MAC, model, type, state, IP, firmware and pending update, uptime,
  CPU, memory, temperature, overheating, PoE used and budget, uplink target, port and speed), then per port: index,
  name, state (`up`, `down`, `disabled`), speed, maximum speed, connector, duplex, PoE watts, rx and tx errors, drops,
  link-down count, STP state, flags, a one-sentence note, and what is connected (up to five entries of kind, name and
  MAC, plus the total count).
- `message`: the sentence the text gives, plus candidate devices (name, IP, MAC) when the query matched several.

**Flags and notes are computed on the server**, in one place, and used by both the text table and the view model, so
the two never disagree. The four existing flags stay and no flag is added:

- **Faults:** `errors`, `flapping`, `half duplex`.
- **Slow:** `slow link` (up at 100 Mb or less), with one change: it is not raised when the port's known maximum is
  100 Mb or less, because the port is then at full speed.

A port running below its maximum is not a flag by itself: a 1 GbE device on a 2.5 or 10 GbE port is normal. The maximum
is shown as a fact (the "Max" column, the panel's Link line) and is used in the note of a port that is already flagged.
"Flagged" keeps its server meaning everywhere outside the view: any flag, as `Issues` counts today.

The note is one plain sentence per flagged port built from its flags and, when known, its maximum, for example "Linked
at 100 Mb half duplex on a port that can do 1 GbE, with receive errors: suspect the cable or the device's network
card." Ports without flags have no note.

**The text answer** gains a "Connected" column and a "Max (Mbps)" column in the ports table. Nothing is removed. This
is so the model has what the view shows when the user presses "Ask Claude why".

### View

One HTML file with three states driven by `view`.

**Protocol.** On load the view sends `ui/initialize`, then waits for `ui/notifications/tool-input` and
`ui/notifications/tool-result`. It renders from `structuredContent`; if that is missing it shows the result's text.
It reports its height with `ui/notifications/size-changed`. It accepts messages only from `window.parent`.

**Theme.** It follows the host's light or dark theme from `hostContext` and re-themes on
`ui/notifications/host-context-changed`. The dark palette is the mockup's (ground `#0F1318`, surface `#171C23`, text
`#E6EAF0`, muted `#9AA6B5`, blue `#5AA9FF`, orange `#FFA24A`, pink-red `#FF7A88`). The light palette keeps the same
roles; its values are the builder's. Fonts are the host's variables when given, otherwise the system stack.

**Devices state.** A grid of tiles: name, model, state, uptime, CPU and memory, temperature, an "update available"
mark, and issue chips. Offline devices sort first, as in the text. A tile is a button: it calls
`device_diagnostics_faq` with `device` set to that device's MAC and the original `siteId`, shows the result as the
device state, and offers Back. The call is the view's own; it adds nothing to the chat.

**Device state.**

```
Core Switch · US24 · online · PoE 21.5 of 95 W · up 2 d              [Refresh]
┌────────────────────────────────────────────────────────────────┐
│   1  3  5  7  9 11    13 15 17 19 21 23        SFP1            │
│   2  4  6  8 10 12    14 16 18 20 22 24        SFP2            │
└────────────────────────────────────────────────────────────────┘
 ■ normal   ■ slow   ■ fault   □ no link   ▁ delivering PoE

 Port 2 · NAS
 Link        100 Mb, half duplex (port can do 1 GbE)
 Connected   nas
 PoE         off
 Errors      1,532 received, 0 sent     Drops 12     Link downs 14
 Counters run since the switch restarted, 2 days ago.
 Linked below what the port can do, with receive errors: suspect the
 cable or the device's network card.
 [Ask Claude why]  [Trace its path]
```

- **Faceplate layout.** Copper ports in two rows, odd numbers on top, in blocks of six columns that wrap when the view
  is narrow. Five ports or fewer sit in one row. Ports whose connector is not copper form a separate group after the
  rest. A device with no ports shows the header only.
- **Port colour.** Pink-red: any fault flag. Orange: `slow link` and no fault. Blue: up with no flag. Outline: no link.
  Dimmed: disabled. Fault and slow ports each carry their own mark, so colour is not the only signal. A bar under the
  port shows it is delivering PoE.
- **Ports are buttons** with a label that reads the port, its name and its state, reachable by keyboard. On load the
  first fault port is selected, else the first slow port; if no port is flagged, nothing is selected and the panel says
  to pick a port.
- **Counter wording.** The console's error, drop and link-down counters run since the device last restarted, not over
  24 hours (the mockup said "24 h"; that was wrong). The panel says so and gives the uptime.
- **Selection reaches the model.** When a device is opened or a port is selected, the view sends
  `ui/update-model-context` with one line naming the device and port, so "why is that one slow" has a referent. A host
  that refuses it is ignored.
- **Refresh** calls the tool again with the same arguments. It is disabled for 10 seconds after each use, matching the
  console cache.

**Buttons that talk to Claude.** Both send `ui/message` with role `user`; the host may ask the user first.

- **Ask Claude why**, on ports with any flag (fault or slow): names the device, the port number, the port's owner-set name, the flags
  and the counters, and asks for the likely cause.
- **Trace its path**, only when exactly one client is connected: asks for the path of the client with that MAC.

Neither includes a client's hostname or display name (see Anti-goals).

**Message state.** The sentence, and candidate tiles when there are any.

**Look.** The sketch above, the field lists and the palette are the whole brief. The mockup the owner picked this from
is a reference for mood (dark, quiet, one accent per state), not a pixel specification, and is not in the repository.
The owner confirms the look on the development page (Verification, item 3) before anything is deployed.

**Reuse.** Later views will need the same two pieces: the view's JSON-RPC client, and the server-side registration of a
view (a URI, an embedded file, the tool's `_meta`). Write each so a second view can use it unchanged. No further
abstraction.

**Failures.** If a tool call made by the view fails or is refused, the view keeps what it was showing and says so in a
line. All values are written with `textContent`; nothing from the result is inserted as HTML.

### Documentation

- README: a short section on the view and which hosts show it. The tool counts stay as they are.
- CHANGELOG: an entry under the next version. The version bump is the owner's.

## Verification

1. **First, before the rest is built:** a throwaway one-line view on `device_diagnostics_faq`, deployed to the owner's
   server, opened in Claude on the web or desktop. This proves the host renders a view from this server at all, and
   that the model still receives the markdown. Claude's web and desktop apps were named as MCP Apps hosts when the
   extension launched; this session did not confirm it against this server. Claude Code's terminal shows text only.
   Also check there that Claude Code still gives the model the markdown and not the JSON; if it does not, move the view
   model to `_meta` (see Approaches).

   This has not been run. It is the plan's first task and a hard stop: the deploy and the look in Claude are the
   owner's, or done with the owner's go-ahead at that point. If Claude does not render the view at all, the work stops
   there and goes back to the owner; the rest is not built against the development page alone.
2. **Unit tests** (xUnit, the existing `Harness`):
   - `tools/list` shows `_meta.ui.resourceUri` on `device_diagnostics_faq` and on no other tool;
   - `resources/read` returns the HTML with MIME type `text/html;profile=mcp-app`;
   - the HTML contains no `http://` or `https://`;
   - each of the three view models has the expected content for the existing fixtures (extended with a client on a
     port, an AP uplinked to a port, and an official-API device with `maxSpeedMbps` and `connector`);
   - the must-not-leak fixture values (`x_vwirekey`, `syslog_key`, the maps key) appear in neither the text nor
     `structuredContent`;
   - a failing clients step and a failing official step each leave the rest of the answer intact;
   - the existing text assertions still pass.
3. **The view by itself:** a development-only page (under `tests/`, not embedded, not shipped) that frames the HTML,
   plays the host side of the protocol and feeds it sample results for all three states in both themes. This is how
   the view is exercised in a browser without a real host.
4. **In the host:** the owner opens the deployed server in Claude and checks tiles, drill-in, port selection, both
   buttons and Refresh against the real network.

## Left to the builder

- Names and exact shape of the view model, and how `DeviceDiagnostics` is split so text and view model share one pass.
- How the embedded HTML is loaded and registered, and whether the resource appears in `resources/list`.
- The light palette, spacing, the flagged-port mark, and port sizes at narrow widths.
- Wording of notes and of the two chat messages, within the rules above.
- If a live `port_table` turns out to carry the port's maximum speed and medium itself, using that and dropping the
  official-API step.
