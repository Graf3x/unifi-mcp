# Contributing

Thanks for helping. Bug reports, fixes, new diagnostics and better docs are all welcome.

## Before you start

- For anything bigger than a small fix, open an issue first so we can agree on the approach.
- Never paste real network data, API keys, certificate files or sign-in codes into issues, commits or test fixtures.
  Test data is synthetic; use `example.com` hosts and `192.168.1.x` addresses.
- Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Build and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet test unifi-mcp.slnx
```

Tests run against a fake UniFi handler; nothing talks to a real console. To try the server against your own console,
follow [guide 1](README.md#guide-1-quick-local-setup) in the README.

## Where things live

| Path | What it is |
|---|---|
| `src/UnifiMcp/ApiTool.cs`, `ReadOperationTool.cs`, `OpenApiCatalog.cs` | Tools generated from the OpenAPI document in `openapi/` |
| `src/UnifiMcp/WriteApproval.cs` | Whether a write may run: the owner's confirmation form, or the `confirm` fallback where the owner opted in |
| `src/UnifiMcp/FaqTools.cs`, `DiagnosticTools.cs`, `DiagnosticTools.Devices.cs` | The task-oriented and diagnostics tools |
| `src/UnifiMcp/DeviceReading.cs` | The device reading: every rule about a console device record, for the text answers and the view alike |
| `src/UnifiMcp/Answer.cs`, `ConsoleJson.cs` | A composite tool's answer (steps, failure lines, tables, the cap, the view) and reading the console's JSON by field name |
| `src/UnifiMcp/ViewResources.cs`, `Views/`, `DeviceViewModel.cs` | Interactive views (MCP Apps): one embedded HTML file per view, and the data a tool hands it in `_meta` |
| `src/UnifiMcp/Redaction.cs` | The credentials redacted from every console answer and error before it reaches the client |
| `src/UnifiMcp/ConsoleTransport.cs` | The only code that talks to the console: the key, certificate trust (pin or CA), scrubbing, and each request's turn, deadline and byte limit |
| `src/UnifiMcp/UpstreamBudget.cs` | What one composite answer may ask of the console in all, and why it stopped early |
| `src/UnifiMcp/UnifiClient.cs` | The console's two APIs over that transport: the write gate, paging, finding a site |
| `src/UnifiMcp/ConsoleReads.cs` | The console's internal API: one named read per endpoint, cached and throttled |
| `src/UnifiMcp/Listeners.cs`, `RouteTable.cs`, `Connector/PublicGate.cs` | Which listener a request came in on, which paths each listener serves, and the gate that refuses the rest |
| `src/UnifiMcp/Connector/Clients.cs` | The Client module: resolving and registering a client, and its redirect rules |
| `src/UnifiMcp/Connector/` | The Claude connector: OAuth, sign-in pages, token issuing |
| `src/UnifiMcp/Setup/` | The setup page: its checks, its markup, script and styles, and the README guide it renders |
| `tests/UnifiMcp.Tests/` | xUnit tests; `ToolContractTests` checks every tool against Claude's tool rules |
| `tests/view-host/` | A development-only page that plays the host for a view, with sample answers |
| `tools/make-diagrams.mjs` | Draws the README diagrams in `docs/images/` |
| `deploy/` | The maintainer's own deployment bundle; you can ignore it |

## Conventions

- **Tools:** snake_case names, a title, read-only or destructive hints, a description that starts with its `[Category]`,
  and a description on every parameter. `ToolContractTests` fails if one is missing. Writes must stay behind
  `UNIFI_ALLOW_WRITES` and `WriteApproval`: the owner's form, showing the whole change. A client that cannot show it is
  refused, unless the owner set `UNIFI_ALLOW_CONFIRM_FALLBACK=true`; only then does `confirm` run a write.
- **Composite tools:** write the answer through `Answer`: put every read the answer can do without in `a.Step`, so its
  failure is reported and the rest still runs, and read the console's JSON through `ConsoleJson`.
- **Devices:** a rule about a console device record (online, its state, a port's flags or look, what is plugged in
  where) goes in `DeviceReading`, and the text and the view render the records it returns. Neither the tools nor the
  view page decide such a thing themselves; `DeviceReadingTests` tests each rule over one record.
- **Clients:** whether a client or a redirect address is acceptable is decided in `Clients.cs`, for registrations and
  metadata documents alike; the OAuth endpoints only translate to and from HTTP. `OAuthClientsTests` tests the rules
  through `Clients`, with no HTTP.
- **Routes:** give every new route an entry in `RouteTable`, with the listeners that serve it. A path with no entry
  is a 404 on every listener before authentication, and `RouteTableTests` fails on a mapped route with no entry, or an
  entry with no route.
- **The console's internal API:** add a read as a named method on `ConsoleSite` in `ConsoleReads.cs`, never a way to
  send a path of the caller's choosing, and return chosen fields, never the raw JSON (it carries secrets).
- **Secrets:** console JSON that reaches the client whole (an operation's answer, an error body) goes through
  `Redaction` first. A new credential field the name rule misses fails `RedactionTests` when the OpenAPI document
  marks it; add it to the rule there.
- **Docs:** if you change setup steps or settings, update the README and `.env.example` together. The setup page renders
  the README's Setup section, and a test checks that every heading it links to still exists. After editing
  `tools/make-diagrams.mjs`, run `node tools/make-diagrams.mjs` and commit the regenerated SVGs.
- **Checksums:** `deploy/SHA256SUMS` covers `src/UnifiMcp/`. If you change files there, regenerate it from the repo root:
  `sha256sum -b $(git ls-files src/UnifiMcp | LC_ALL=C sort) > deploy/SHA256SUMS`. If you can't, say so in the pull
  request and the maintainer will.
- **Commits:** short imperative subject with a type prefix, e.g. `fix(diagnostics): ...` or `docs: ...`.

## Pull requests

Keep each one to a single change, make sure `dotnet test` passes, and add a line to `CHANGELOG.md` under Unreleased.
