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
| `src/UnifiMcp/FaqTools.cs`, `DiagnosticTools.cs`, `DiagnosticTools.Devices.cs` | The task-oriented and diagnostics tools |
| `src/UnifiMcp/ViewResources.cs`, `Views/`, `DeviceViewModel.cs` | Interactive views (MCP Apps): one embedded HTML file per view, and the data a tool hands it in `_meta` |
| `src/UnifiMcp/ConsoleTransport.cs` | The only code that talks to the console: the key, certificate trust (pin or CA), scrubbing |
| `src/UnifiMcp/UnifiClient.cs` | The console's two APIs over that transport: the write gate, paging, finding a site |
| `src/UnifiMcp/ConsoleReads.cs` | The console's internal API: one named read per endpoint, cached and throttled |
| `src/UnifiMcp/Connector/` | The Claude connector: OAuth, sign-in pages, token issuing |
| `src/UnifiMcp/Setup/` | The setup page: its checks, its markup, script and styles, and the README guide it renders |
| `tests/UnifiMcp.Tests/` | xUnit tests; `ToolContractTests` checks every tool against Claude's tool rules |
| `tests/view-host/` | A development-only page that plays the host for a view, with sample answers |
| `tools/make-diagrams.mjs` | Draws the README diagrams in `docs/images/` |
| `deploy/` | The maintainer's own deployment bundle; you can ignore it |

## Conventions

- **Tools:** snake_case names, a title, read-only or destructive hints, a description that starts with its `[Category]`,
  and a description on every parameter. `ToolContractTests` fails if one is missing. Writes must stay behind
  `UNIFI_ALLOW_WRITES` and the per-call `confirm`.
- **The console's internal API:** add a read as a named method on `ConsoleSite` in `ConsoleReads.cs`, never a way to
  send a path of the caller's choosing, and return chosen fields, never the raw JSON (it carries secrets).
- **Docs:** if you change setup steps or settings, update the README and `.env.example` together. The setup page renders
  the README's Setup section, and a test checks that every heading it links to still exists. After editing
  `tools/make-diagrams.mjs`, run `node tools/make-diagrams.mjs` and commit the regenerated SVGs.
- **Checksums:** `deploy/SHA256SUMS` covers `src/UnifiMcp/`. If you change files there, regenerate it from the repo root:
  `sha256sum -b $(git ls-files src/UnifiMcp | LC_ALL=C sort) > deploy/SHA256SUMS`. If you can't, say so in the pull
  request and the maintainer will.
- **Commits:** short imperative subject with a type prefix, e.g. `fix(diagnostics): ...` or `docs: ...`.

## Pull requests

Keep each one to a single change, make sure `dotnet test` passes, and add a line to `CHANGELOG.md` under Unreleased.
