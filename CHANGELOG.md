# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions
follow [Semantic Versioning](https://semver.org/).

## Unreleased

### Added

- `UNIFI_WRITE_TOOLS` limits which write tools exist when writes are on: an exact, comma or space separated list of
  write tool names. The others are not listed and cannot be called. A name that is not a write tool stops startup with
  the list of valid names, even while writes are off, so a typo shows before writes are turned on. Unset, every write
  tool is on, as before. `getting_started` says when the owner limited them.
- A misspelt `UNIFI_*` or `MCP_*` environment variable is no longer ignored without a word: startup logs a warning
  naming it (never its value) and, when one is close, the setting it probably meant. The name is checked in any case,
  since on Linux `mcp_setup_page` is never read. When the typo is what stops startup (a misspelt `MCP_LAN_ACCESS_TOKEN`
  makes `MCP_LAN_LISTEN` refuse to start), the warning is printed next to the error.

### Changed

- The README opens with what the server does: finished answers, write approval, redaction, pinned TLS, the connector
  with no open ports, its limits on the console and its short tool list.

### Maintenance

- Dependabot opens weekly updates for the NuGet packages, both Dockerfiles (always together) and the workflow actions.
  A weekly vulnerability audit (also on pull requests) fails when any direct or transitive package has a known
  advisory; normal builds and the Fleet deploy never fail on one. Every workflow action is pinned to a commit.

## 0.11.0

Two changes need you to act when you update. With a Cloudflare tunnel, move the public socket to its own folder
(README, Updating) or the connector stays down. With writes on, a client that cannot show the confirmation form is now
refused every write unless you set `UNIFI_ALLOW_CONFIRM_FALLBACK=true`.

### Security

- Write tools need a person's approval by default. A client that cannot show the confirmation form (MCP 2026-07-28
  form elicitation) is refused every write, and `confirm` no longer runs it. The old `confirm` fallback is still there
  if you set `UNIFI_ALLOW_CONFIRM_FALLBACK=true`; with it, the model approves its own writes on such clients.
- The confirmation form shows the whole change, every argument and the full body, with nothing cut. A change too big
  for the form (over 10,000 characters) is refused and nothing is sent.
- Credentials the console sends back (Wi-Fi passphrases, per-network keys, RADIUS secrets, the internal API's `x_`
  fields, hotspot voucher codes) are replaced with `[redacted]` in every answer, echoed write and error, writes on or
  off. Names, ids and other settings still show. An answer that is not JSON is withheld with a short line saying so. A
  write whose body carries `[redacted]` is refused, so putting back an object read that way cannot set a passphrase to
  the mark.
- The public socket has a folder of its own (`./run-public` on the host, `/run/unifi-mcp-public/public.sock` in the
  container), and both tunnel Compose files mount only that folder into the tunnel container, so it can no longer
  reach the tailnet socket. The server warns when the two sockets share a folder.
- At startup the server removes only a stale socket at a configured socket path. An ordinary file, a link or a live
  listener is left alone and the start stops with a message naming the path. A lock file next to each socket stops a
  second server on the same folder from unlinking the first one's socket.
- A site name or internal name that more than one site has is refused, with each candidate's name and id; an id still
  wins. A write looks a site name up again on every call, so a renamed site whose old name passed to another site
  cannot receive the change, and a target that changes while the form is open is asked about again.
- The images run ASP.NET Core 10.0.15 (from 10.0.9) and build with SDK 10.0.404.

### Added

- `UNIFI_REVEAL_SECRETS=voucher-codes` shows hotspot voucher codes, for deployments that hand them out through Claude.
  Any other value stops the server at startup. Wi-Fi passphrases and keys cannot be revealed.
- `UNIFI_ALLOW_CONFIRM_FALLBACK`, described above.
- README: "Backup and rollback" (back up `./data` before an update, keep `UNIFI_MCP_DATA_KEY` apart, record the image
  digest, roll back image and data together) and "Sharing one server" (one console key for every caller and site, who
  each way in identifies, how to revoke each credential, separate instances for groups that don't trust each other).
- CI starts every image through `tools/image-gate.sh` on amd64 and arm64: a healthy start as a non-root user on a
  read-only filesystem, a setup listener that serves only `/setup`, private sockets, and the public socket in its own
  folder. A release pushes by digest, gates that digest, and only then tags it. A Linux Compose test checks the tunnel
  container can neither see nor open the tailnet socket. CI also checks `deploy/SHA256SUMS` against the sources.

### Changed

- `oauth.db` moves to schema version 2. On upgrade, outstanding authorization codes are expired (clients just connect
  again) and consents of already revoked grants are removed. Registrations, live grants and the signing key are kept.
- Every console call is bounded: at most 4 requests at the console at once, 30 seconds and 16 MB per request (wait and
  body included, declared length or not), 5,000 items per paged list. One composite answer makes at most 100 requests
  and reads at most 32 MB; when it stops early it starts with "Partial answer" and says how to narrow the question. A
  device view over 256 KB is replaced by a message asking for one device.

### Fixed

- An authorization code mints only into the grant of the consent that issued it, while that consent stands, so a code
  from before a disconnect and reapproval yields no tokens.
- Revoking a grant (token reuse, a client's revoke, an owner no longer allowed, disconnect) withdraws its consent too,
  so the client must be approved again even after the 60-day record of the revocation is swept.
- A refresh, a code under a standing consent and an approval each commit as one transaction. A failed write leaves the
  presented refresh token usable, and a disconnect or reapproval at the same moment is no longer overwritten.
- A loopback redirect must match the declared address exactly apart from its port. Redirects with a fragment or user
  info are refused at registration, in metadata documents and on authorize.
- Asking `system_log` the same question again within ten seconds no longer spends the console budget twice, and the
  counts cover the same window as the events. The internal API's cache and budget follow the steady clock, so setting
  the system clock neither keeps stale answers nor empties the budget.
- When a network's details cannot be read, `dhcp_reservation_faq` and `who_is_on_network_faq` say its subnet membership
  is unknown and name what could not be read, instead of calling a client outside every subnet.

## 0.10.0

### Added

- Write tools ask you to approve each change with a confirmation form (MCP elicitation, 2026-07-28) when the client can
  show one, as Claude Code does. The form shows the change, the exact request (site and ids resolved) and every
  argument, with a long body cut short after them (the form says the rest is sent too); Accept or Decline. Values are
  shown escaped, so none can add lines of its own. `confirm` is ignored there, so the model cannot approve a write for
  you. Declining sends nothing and tells the model not to retry. An answer is good once, for that
  tool, request, those exact arguments and you, and lapses after ten minutes; any other answer gets a fresh form.
- The audit log says how each write was approved (`approved by form` or `approved by confirm argument`, also when
  the console gave no answer or the call was cancelled while waiting for it), or that you said no (`declined by form`, `cancelled on form`). A call that stopped to
  ask you logs `asked` instead of `failed`, and an answer that did not match its form is logged before asking again.

### Changed

- `confirm` is optional on write tools: it is the fallback for clients that cannot show the form, and still required
  there. A write's arguments are checked and its site looked up before it asks, so the preview shows the path that
  would be sent, and a call missing an argument, or with one the tool does not take, is refused without asking.

## 0.9.0

### Added

- Sign-in redirects back to Claude now name this server as their issuer (`iss`, RFC 9207), on success and on error,
  and the authorization server metadata says so, as the 2026-07-28 MCP spec asks. A client that checks `iss` can
  refuse an answer that came from the wrong server; a client that ignores it is unaffected. A redirect address
  registered with its own query keeps it, less any `code`, `state`, `iss` or error it held, so the answer is only ours.

## 0.8.0

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
