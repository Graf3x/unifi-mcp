# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions
follow [Semantic Versioning](https://semver.org/).

## Unreleased

Nothing yet.

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
