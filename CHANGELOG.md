# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions
follow [Semantic Versioning](https://semver.org/).

## Unreleased

### Added

- `device_diagnostics_faq` finds a device by its model or type when no name, IP or MAC matches, so "udm", "UDM Pro"
  or "USW" opens that device (or lists every device it fits). A name match still comes first.

### Fixed

- `device_diagnostics_faq`'s summary no longer flags a port as a slow link when it runs at all the port can do (a
  100 Mb-only port read "port(s) 4 flagged" on the summary and its tile, and had no flag once the device was opened).
  The summary flags errors, flapping and half duplex; a slow link shows in the device's own answer.
- `device_diagnostics_faq` no longer shows an offline device's last uptime in its text answer; the summary and the view
  already left it out.
- A port that is down has no speed in `device_diagnostics_faq`'s text port table, as in the view; it used to show the
  last speed the console reported.
- A composite tool (the `*_faq` tools, `system_log`, `list_port_forwards`) that cannot answer at all, because it is not
  configured, names a site the console doesn't have or loses a read it can't do without, returns the reason as a tool
  error (`isError`), after whatever it had already written, and the audit line says `-> error`. Before, the faq tools and
  most diagnostics gave the reason as a successful answer. A failed step is still not a tool error, and
  `device_diagnostics_faq` still shows the reason in its view.
- A console answer with a field of a type this server doesn't expect fails only the step that read it ("The console's
  answer was not what this server expects.") instead of ending the tool with an unhandled error.
- `client_path_faq` and `dhcp_reservation_faq` say why when the console's own record of a client can't be read
  (`Console client record step failed`). Before, that read the same as a client the console has no record of.
- Tables use one line ending, and a `|` or a line break in a name or header no longer breaks them. `search_operations`
  uses the same table, so a `|` in an operation's summary no longer splits its row.
- The tailnet listener serves only `/mcp`, `/healthz` and the connector icons. It used to refuse a list of the public
  side's paths and let the rest through, so the protected-resource metadata (`/.well-known/oauth-protected-resource`)
  answered there, and a public route added later would have too.
- `/mcp/` and `/healthz/` work on the public listener, as they already did on the LAN and the tailnet (the 0.6.0 note
  said so). `/oauth/authorize/` sends a browser to sign-in like `/oauth/authorize`, instead of a bare 401.

### Changed

- `system_log` reports counts it can't read as `**Counts step failed:**`, like every other step, instead of
  `(Counts unavailable: …)`.
- A composite tool's answer (its steps, failure lines, tables, the length cap and the view) is written by one module,
  `Answer`, and the console's JSON is read by field name through one, `ConsoleJson`. Apart from the fixes above, answers
  are unchanged.
- The public listener answers only the paths it serves: `/.well-known` is its three documents, and a path that only
  starts like a route (`/connectionsX`, `/mcp/icon-x.png`) is a 404 at the door instead of a 403 from the origin check.
  MCP discovery is unchanged. Which listener serves which path is one table, `RouteTable`, that the gate, the request
  guard and sign-in all read.

## 0.7.0

### Added

- `device_diagnostics_faq` carries an interactive view (MCP Apps) for hosts that render one: a tile per device, and for
  one device a switch faceplate where each port shows its link, errors, PoE and what is connected to it. Hosts without
  views get the same answer as text. The view makes no writes and loads nothing from the network.
- `device_diagnostics_faq` for one device lists what is connected to each port ("Connected") and the port's maximum
  speed ("Max (Mbps)").
- Every tool call writes an audit line (`UnifiMcp.Audit`): login, tool and outcome, never arguments. Before, only
  `call_read_operation` and the write tools did. Writes log at Information, reads at Debug.

### Fixed

- The internal API's throttle and cache, and `system_log`'s time window, read the server's clock instead of their own.
- On a console with several sites in different time zones, each site's times are shown in its own zone. Before, every
  site used the zone of the first one asked.
- `device_diagnostics_faq` no longer spends a call of the internal API's budget on the console's time zone, which it
  never shows. Every diagnostic asks for the zone only when it prints times.
- When several requests are refused at once because the console's certificate doesn't match the pin (or isn't trusted
  in CA mode), each says which certificate the console presented and where to change the pin. Before, most got a bare
  "Could not reach the console". A console that hangs up mid-handshake is no longer blamed on its certificate.

### Changed

- For one device, `device_diagnostics_faq` no longer flags `slow link` on a port linked at 100 Mb when 100 Mb is all
  the port can do.
- A prerelease tag (`v1.2.3-rc.1`) publishes only its own image version and no longer moves `latest`.
- The OAuth grant rules (consent, refresh-token families, rotation, revocation, disconnect) live in one module,
  `GrantLedger`. No behaviour changes.
- Whether an identity is the owner is decided in one place, `OwnerAdmission`, for the sign-in cookie, the Google
  sign-in, code exchange and refresh. Authorize and consent no longer check it a second time.
- Log reasons for a refused owner say `NotListed` (a Google address not on the list) or `OtherSignIn` (an identity
  from the sign-in method `MCP_SIGNIN` no longer names) instead of `NotAllowed`. A refused refresh no longer says
  "allow-list" in code mode.
- The console's internal API is read through `ConsoleReads`: one named read per endpoint for a site, so no tool builds
  an internal-API path. The reads it offers are the only internal requests the server can make, which replaces the
  allow-list of path patterns. Throttle and cache are unchanged.
- A site is found the same way everywhere, by its id, name or internal name, with one lookup. A site UUID the console
  doesn't have now gets "The console has no site" and the list of sites, instead of the console's 404.
- How a request reaches the console (the key, the timeout, certificate trust and the refusal message) lives in one
  module, `ConsoleTransport`. The handshake and the setup page's certificate check use the same trust rule.

## 0.6.1

### Changed

- README: the setup-paths diagram shows guide 4, Claude Code on another machine on your network.

### Fixed

- The container health check no longer logs a "Refused caller (DenyNoIdentity)" warning every 30 seconds. The tailnet
  listener now logs a refused caller only when the endpoint needs one, so refusals on `/mcp` are still warnings and
  `/healthz` is quiet. ([#4](https://github.com/Graf3x/unifi-mcp/issues/4))

## 0.6.0

### Added

- A token-only LAN listener, off by default (`MCP_LAN_LISTEN`, `MCP_LAN_ACCESS_TOKEN`): Claude Code on other machines on
  your network can connect with no tunnel, domain or Tailscale. It serves only `/mcp` and `/healthz`, accepts only its
  own token, and the server refuses to start with it on and no token. `docker-compose.lan.yml` and guide 4 in the README
  set it up. `/mcp/` with a trailing slash works too. Suggested in [#1](https://github.com/Graf3x/unifi-mcp/issues/1).
- The setup page knows the LAN setup: guide 4 skips the connector, tunnel and sign-in checks, and a LAN check shows the
  `claude mcp add` command.
- A container health check: `dotnet unifi-mcp.dll --healthcheck` asks `/healthz` on `mcp.sock`, and the image runs it, so
  `docker ps` shows whether the server is up.

## 0.5.1

### Fixed

- The image: `.dockerignore` kept the README's diagrams out of the Docker build, so 0.5.0 built no image. 0.5.1 is
  0.5.0 with that fixed; pull requests and pushes to main now build the image too.

## 0.5.0

### Added

- A setup page, on its own listener at `http://localhost:5198` and opened from a link in the startup log. It tests
  each setting live (the console, its certificate, the API key, the site, the connector, the tunnel from the outside,
  the sign-in and the tailnet logins) and says what to fix, shows the sign-in code as soon as Claude's sign-in page
  asks for one, and carries the README's setup guides with the server's own values filled in. On unless
  `MCP_SIGNIN=google` with a public URL; `MCP_SETUP_PAGE` and `MCP_SETUP_LISTEN` change that.
- README: screenshots of the setup page.

## 0.4.0

### Added

- `UNIFI_CERT_SHA256` accepts the SHA-256 of the console's public key as well as of its certificate, so a re-issued
  certificate that keeps its key needs no change.
- `UNIFI_CERT_SHA256` takes several fingerprints, comma separated, so a new pin can go in before the old one leaves.
- `UNIFI_CERT_TRUST=ca` checks a certificate from a trusted CA the usual way, for consoles that have one.
- A pin mismatch names both fingerprints the console presented.
- README: three setup guides with diagrams, example prompts, a settings reference and an FAQ. Contributing guide,
  security policy, code of conduct, issue and pull request templates.

## 0.3.0

First public release.

- Tools for all 73 operations of the UniFi Network Integration API (v11.0.97): the 41 reads through
  `call_read_operation`, the 32 writes as separate destructive tools, listed only with `UNIFI_ALLOW_WRITES=true` and
  each needing a `confirm`.
- Task tools: `who_is_on_network_faq`, `client_path_faq`, `dhcp_reservation_faq`, `network_health_faq`.
- Diagnostics from the console's internal API, rate limited and cached: `system_log`, `internet_health_faq`,
  `wifi_health_faq`, `client_diagnostics_faq`, `device_diagnostics_faq`, `list_port_forwards`.
- Claude connector over a Cloudflare tunnel with Google or one-time-code sign-in, an optional fixed access token, and
  optional Tailscale access.
- Multi-arch image at `ghcr.io/graf3x/unifi-mcp`.
