# UniFi Network MCP

An MCP server for the UniFi Network API (v11.0.97), generated from the OpenAPI document in `openapi/`. Built with .NET 10
and the official `ModelContextProtocol` SDK. It runs as a container next to your UniFi console and can be added to Claude
(claude.ai, Claude Desktop, the phone apps) as a custom connector. You sign in with a one-time code from the server's log
or with your Google account, and clients that send a fixed header can use an access token instead.

- All 73 API operations. The 41 reads run through one tool, `call_read_operation` (its `operation` lists them), which
  keeps the tool list short: Claude picks tools less well past 30 to 50. The 32 writes are a destructive tool each,
  listed only when writes are on. `search_operations` finds an operation and `describe_operation` shows its schema.
  Read-only, the server lists 15 tools; with writes on, 47.
- 4 task-oriented composites: `who_is_on_network_faq`, `client_path_faq`, `dhcp_reservation_faq`, `network_health_faq`.
- 6 read-only diagnostics the official API does not cover, from the console's internal API (the one its own UI uses):
  `system_log`, `internet_health_faq`, `wifi_health_faq`, `client_diagnostics_faq`, `device_diagnostics_faq`,
  `list_port_forwards`. They also give `client_path_faq` the switch port and `dhcp_reservation_faq` the fixed IP.
- 4 guide tools: `getting_started`, `unifi_status`, `search_operations`, `describe_operation`.

## Safety

- Read-only by default. Write tools exist only when `UNIFI_ALLOW_WRITES` is exactly `true`; each is marked destructive and
  needs `confirm` set to its own tool name, otherwise it returns a preview and sends nothing.
- TLS to the console uses a SHA-256 certificate pin. There is no default pin and no way to turn verification off.
- The API key comes from the `UNIFI_API_KEY` environment variable, falling back to `/config/unifi-api-key` only when the
  variable is unset or empty. It is never logged or returned, and is scrubbed from console errors.
- No published ports. The server listens on two Unix sockets:
  - `public.sock`, the Claude connector, reached through a Cloudflare tunnel. It takes OAuth bearer tokens, issued only
    after the owner signs in (see [Sign-in](#4-choose-how-you-sign-in)), and, if you set one, the fixed `MCP_ACCESS_TOKEN`.
  - `mcp.sock`, optional tailnet access: reached through `tailscale serve`, which stamps `Tailscale-User-Login`; the server
    checks it against an allow-list. The public socket ignores that header.

- The internal API is reached only for a fixed list of reads (anything else is refused before a request is made, writes
  on or off), and is throttled: one request at a time, identical reads within 10 s served from cache, and at most 30
  calls a minute after a burst of 20. That API was not built for outside tools, and frequent polling of it can make a
  console drop packets. Its answers carry secrets (device keys, a maps API key), so these tools return chosen fields
  only, never the raw JSON.

Until the key and pin exist the server runs and reports "not configured" without contacting the console. Until the
connector is fully configured the public socket answers 503; the startup log line names what is missing (names only).

## Run it yourself

You need: a UniFi console running UniFi Network with the Network API, a machine on the same network with Docker and
Docker Compose, and a domain on Cloudflare (free plan is fine). Placeholders below: `unifi.example.com` is the hostname
you pick, `you@example.com` is your Google address (only for Google sign-in), `192.168.1.1` is your console.

### 1. Get the files

```sh
git clone <this repository> unifi-mcp && cd unifi-mcp
cp .env.example .env
mkdir -p data run
```

Only `docker-compose.yml` and `.env.example` are needed at run time if you would rather copy those two files. Create
`data` and `run` as yourself: the containers run as `PUID:PGID` from `.env` (your `id -u` / `id -g`) and must be able to
write there.

### 2. UniFi API key

In UniFi Network, open Settings, Control Plane, Integrations and create an API key (the menu may move between versions).
Put it in `.env` as `UNIFI_API_KEY`, and set `UNIFI_URL` to the console's address (`https://192.168.1.1`).

### 3. The console's certificate fingerprint

The console uses a self-signed certificate, so the server pins its SHA-256 fingerprint instead of trusting a CA. Read it
from the network:

```sh
openssl s_client -connect 192.168.1.1:443 </dev/null 2>/dev/null | openssl x509 -noout -fingerprint -sha256
```

then confirm it on the console itself (SSH in and run
`openssl x509 -in /data/unifi-core/config/unifi-core.crt -noout -fingerprint -sha256`), so you are not pinning something
in the middle. Put the value in `.env` as `UNIFI_CERT_SHA256` (colons are fine).

When the console regenerates its certificate (often after a firmware update), tool calls fail with "does not match the
pin" and name the fingerprint it presented. Confirm it on the console, update `UNIFI_CERT_SHA256`, and run
`docker compose up -d`.

### 4. Choose how you sign in

When Claude connects, it opens a browser page on your server where you sign in once and approve. `MCP_SIGNIN` picks how:

- **`code`** (what `.env.example` sets): the page asks for a one-time code that the server writes to its log. Run
  `docker compose logs unifi-mcp` and look for `Sign-in code 01234-56789`. A code works once, expires after 10 minutes,
  and is thrown away after 5 wrong tries; at most 10 new codes are issued per hour. Being able to read the server's log
  is what proves you are the owner, so nothing else needs setting up. To end access, use Disconnect on `/connections`.
- **`google`** (the server's default when `MCP_SIGNIN` is unset): sign in with a Google account on an allow-list. In the
  Google Cloud Console: create a project; set the OAuth consent screen (Google Auth Platform) to External and publish it
  ("In production"; the basic scopes `openid email profile` need no review); then create an OAuth client of type Web
  application with the authorised redirect URI `https://unifi.example.com/signin-google`. Put the client ID and secret in
  `.env` as `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`, and your address in `MCP_ALLOWED_GOOGLE_EMAILS` (comma separated
  for more than one). Removing an address ends its access.

Changing `MCP_SIGNIN` later signs everyone out and ends the connections made under the other mode; connect again.

**Optional: a fixed access token.** Some clients send a static header instead of signing in: Claude's connector dialog
has a Request headers section (in beta for some organisations), and Claude Code takes `--header`. Set
`MCP_ACCESS_TOKEN` to a long random value (`openssl rand -hex 32`, at least 32 characters) and send it as
`Authorization: Bearer <token>` or `X-Api-Key: <token>`. It works next to the sign-in above, only on `/mcp`, and anyone
holding it has the same access as you; to revoke it, change or clear it and run `docker compose up -d`.

```sh
claude mcp add --transport http unifi https://unifi.example.com/mcp --header "Authorization: Bearer <token>"
```

### 5. Cloudflare tunnel

In Cloudflare Zero Trust, Networks, Tunnels: create a Cloudflared tunnel and copy its token (the long value after
`--token` in the Docker command shown). Put it in `.env` as `TUNNEL_TOKEN`. The wizard waits for a connection; you can
leave it and come back under Networks, Tunnels.

On the tunnel, add a published application route (public hostname): `unifi.example.com`, Path empty, Service URL
`unix:/run/unifi-mcp/public.sock`, defaults otherwise.

Make sure nothing on that hostname asks for a login or a challenge: no Access application (watch for a wildcard such as
`*.example.com`), no WAF, bot-fight or challenge rule. Claude's servers cannot pass one.

### 6. The rest of `.env`, then start

```sh
openssl rand -base64 32   # paste as UNIFI_MCP_DATA_KEY, once; keep it
```

Set `MCP_PUBLIC_BASE_URL=https://unifi.example.com`. `UNIFI_ALLOW_WRITES=false` keeps the server read-only; see
[Writes](#writes). Every variable is described in `.env.example`.

```sh
docker compose up -d
docker compose logs unifi-mcp
```

The startup line should read `configured, writes off, ... connector ready, sign-in code, access token off`. If it says `not configured (...)` it lists what
is still missing. `UNIFI_MCP_DATA_KEY` wraps the OAuth signing keys in `./data`: a different value cannot open them (the
connector answers 503 and nothing is overwritten). To start over, stop the app and delete `./data`; every client then has
to connect again.

### 7. Add the connector in Claude

In Claude: Settings, Connectors, Add custom connector, URL `https://unifi.example.com/mcp`. Sign in (the code from the log,
or Google), approve, and ask Claude to call `getting_started`. Claude stays signed in (1-hour tokens refreshed in the
background; a refresh token lasts 30 days from its last use). `https://unifi.example.com/connections` lists connected
clients, each with a Disconnect button. With Google sign-in, removing an address from `MCP_ALLOWED_GOOGLE_EMAILS` (and
running `docker compose up -d`) also ends its access.

### Updating

```sh
docker compose pull && docker compose up -d
```

To build from your checkout instead of pulling the published image, uncomment `build: .` and `pull_policy: build` in
`docker-compose.yml`, then `docker compose up -d --build`.

### Troubleshooting

- 503 from `https://unifi.example.com/mcp`: the connector is not configured or its keys cannot be opened; the startup log
  line says why.
- 421: the request reached the server under another hostname. `MCP_PUBLIC_BASE_URL` must match the tunnel's public
  hostname exactly.
- The tunnel never connects: the host may be blocked from Cloudflare on outbound port 7844.

### Optional: Claude Code over Tailscale

`./run/mcp.sock` serves the same tools to your tailnet. Set `MCP_ALLOWED_LOGINS` to the Tailscale logins allowed, then on
the host:

```sh
sudo tailscale serve --bg --https=7420 unix:$PWD/run/mcp.sock
claude mcp add --transport http unifi https://<machine>.<tailnet>.ts.net:7420/mcp
```

See `.mcp.json.example` for the project form.

## Writes

With `UNIFI_ALLOW_WRITES=true` the write tools appear; each still previews until called with `confirm` set to its own
name. Set it back to `false` and restart to make the server read-only.

## Owner's Fleet setup

The owner runs this in their own homelab under Fleet, from the bundle in `deploy/` (`fleet.yaml`, `compose.yaml`,
`Dockerfile`, `SHA256SUMS`). Fleet builds it from checksummed source (`deploy/Dockerfile`, context `./build`), and
supplies values as owner keys set with `fleet-key`; the public hostname is set in `deploy/compose.yaml`. It uses Google sign-in (`MCP_SIGNIN` unset) and no access token. None of this is
needed to run it yourself. After changing anything under `src/UnifiMcp/`, regenerate `deploy/SHA256SUMS` from the repo
root and check it:

```sh
sha256sum -b $(git ls-files src/UnifiMcp | LC_ALL=C sort) > deploy/SHA256SUMS && sha256sum -c deploy/SHA256SUMS
```

### Who does what

Claude never sees key values and holds no Google or Cloudflare credentials. Everything below marked **owner** is the owner's
to run: every `fleet-key` command, the Google Cloud Console, the Cloudflare dashboard, deleting files in `config/`, and any
UniFi firewall change. Fleet releases (the owner's merge) gate each deploy.

### Owner setup (on fleet-mini)

The Network API key, the console's certificate pin, and the Google and Cloudflare values are owner keys set with
`fleet-key`. Allow-lists live in files in `/srv/fleet/apps/unifi-mcp/config` (run as a user with sudo).

```powershell
# The console's certificate SHA-256 (64 hex digits, colons allowed), after confirming it on the console:
#   ssh to the console, then: openssl x509 -in /data/unifi-core/config/unifi-core.crt -noout -fingerprint -sha256
.\fleet-key set UNIFI_CERT_SHA256
.\fleet-key allow UNIFI_CERT_SHA256 unifi-mcp
```

When the console regenerates its certificate, run `fleet-key set UNIFI_CERT_SHA256` again with the new fingerprint;
Fleet redeploys the app with the new value. `config/cert-sha256` still works as a fallback while the key is unset.

```sh
cd /srv/fleet/apps/unifi-mcp/config
# Tailscale logins allowed on the tailnet address, one per line.
echo 'you@example.com' | sudo tee allowed-logins >/dev/null
# Publish on the tailnet. 7420 is already open to tailnet admins in Fleet's ACL.
sudo tailscale serve --bg --https=7420 unix:/srv/fleet/apps/unifi-mcp/run/mcp.sock
```

Then, for Claude Code: `claude mcp add --transport http unifi https://fleet-mini.<tailnet>.ts.net:7420/mcp`.

Files are read on each use, so no restart is needed. Writes are on in `deploy/compose.yaml` (`UNIFI_ALLOW_WRITES: "true"`);
set it to `"false"` and redeploy to make the server read-only.

### Runbook: two stages

The keys arrive in two deploys, so a tailnet-only fix is never blocked on the Google or Cloudflare setup (Fleet refuses to
deploy an app that declares a key that is unset or not granted to it). `<host>` is the public hostname in
`deploy/compose.yaml`.

#### Stage 1: API key from Fleet, writes on, tailnet only (v0.2.0)

`fleet.yaml` declares `keys: [UNIFI_API_KEY]`; `compose.yaml` passes `UNIFI_API_KEY: ${UNIFI_API_KEY}` and sets
`UNIFI_ALLOW_WRITES: "true"`. `MCP_PUBLIC_BASE_URL` stays unset, so the public socket answers 503.

1. Owner: `fleet-key set UNIFI_API_KEY` (if not set yet), then `fleet-key allow UNIFI_API_KEY unifi-mcp`.
2. Deploy through Fleet (the owner merges the release). Check the logs for `configured, writes ON` and
   `connector not configured (...)`, and have Claude call `getting_started`: it should say configured.
3. Owner: delete `config/unifi-api-key`, then restart the app. The API key now comes only from the environment
   (the file is read only when `UNIFI_API_KEY` is unset or empty). Confirm the logs still say `configured`.

#### Stage 2: the connector

1. Owner, Google Cloud Console: as in the `google` option of [Choose how you sign in](#4-choose-how-you-sign-in), with redirect URI
   `https://<host>/signin-google`. Store the client ID and secret with `fleet-key set GOOGLE_CLIENT_ID` and
   `fleet-key set GOOGLE_CLIENT_SECRET`, then `fleet-key allow` each for `unifi-mcp`.
2. Owner, Cloudflare Zero Trust: as in [Cloudflare tunnel](#5-cloudflare-tunnel), route `<host>` to
   `unix:/run/unifi-mcp/public.sock`. Store the token with `fleet-key set UNIFI_MCP_TUNNEL_TOKEN` and `fleet-key allow` it.
3. Owner: put your Google address in `config/allowed-google-emails`, one per line:
   `echo 'you@example.com' | sudo tee allowed-google-emails >/dev/null`.
4. Owner, only if the tunnel will not connect: fleet-mini may be blocked from Cloudflare on outbound port 7844. Opening it
   is a UniFi firewall change for the owner to approve.
5. Deploy 2 (the owner merges the release). It adds the three keys to `keys:`, `secrets: [UNIFI_MCP_DATA_KEY]` (Fleet
   generates it), `MCP_PUBLIC_BASE_URL`, `./data:/data` and the `cloudflared` sidecar. No ports are published.
6. In Claude: Settings, Connectors, Add custom connector, URL `https://<host>/mcp`. Sign in with Google, approve,
   and ask Claude to call `getting_started`.

#### Allow-list behaviour

- `config/allowed-google-emails` (one address per line) is re-read on every use. Removing an address ends access: the next
  refresh fails, and its sign-in cookie stops opening `/connections` and `/oauth/authorize` at once.
- Exception: if the file is empty or missing at startup, the connector reports "not configured" and stays that way until the
  app is restarted, even after you add an address.
- `MCP_ALLOWED_GOOGLE_EMAILS` (a comma or newline separated list) overrides the file. `MCP_ALLOWED_GOOGLE_EMAILS_FILE`
  moves the file.
- If `UNIFI_MCP_DATA_KEY` is missing or cannot open the existing keyring in `./data/keys`, or the Google keys or base URL are
  missing, the public socket refuses everything (503) and the log names what is wrong; the tailnet side keeps working.
  Nothing is regenerated over old keys.

## Develop

`dotnet test unifi-mcp.slnx`. Tests run against a fake UniFi handler; nothing here talks to a real console.
For a local run: `MCP_LISTEN=http://127.0.0.1:5199 MCP_ALLOW_LOOPBACK_TCP=true MCP_ALLOWED_LOGINS=you@example.com dotnet run --project src/UnifiMcp`.

Pushing a `v*` tag runs `.github/workflows/image.yml`: tests, then a multi-arch (amd64, arm64) image pushed to
`ghcr.io/<owner>/unifi-mcp` as the version, `<major>.<minor>` and `latest`. Bump `<Version>` in `src/UnifiMcp/UnifiMcp.csproj`
to match.

## License

TODO(owner): choose a license before publishing. Until one is added, no license is granted.
