# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions
follow [Semantic Versioning](https://semver.org/).

## Unreleased

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
