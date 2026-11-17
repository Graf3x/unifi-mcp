# unifi-mcp as a Claude connector: public address and Google sign-in

Status: draft, awaiting owner approval. Date: 2026-11-12.

## Intent

The owner wants the UniFi tools in their Claude subscription (Claude Desktop, claude.ai and the phone app), not only in
Claude Code on the tailnet. Claude's custom connectors connect from Anthropic's cloud, so unifi-mcp needs a public
HTTPS address and its own OAuth sign-in. The owner liked how the Data Manager project does this and wants the same
experience: add the connector by URL, sign in once in a browser, approve once, and Claude stays signed in.

Data Manager's code is the owner's IP (via Data Disruptors LLC) and may be reused freely. Its MCP auth lives in
`Viridian-Education/Data-Manager` (branch `master`) under `Viridian.DataManagement.Api/Mcp/` (readable with `gh api`):
`OAuth/McpOAuthEndpoints.cs`, `OAuth/McpTokenIssuer.cs`, `OAuth/ClientMetadataDocuments.cs`, the `IMcpOAuthStore`
interface and `McpHostingExtensions`. Port these. Replace what is tied to Viridian (Cosmos, Azure Blob, Viridian Auth).

## Decided by the owner

- **Sign-in is Google**, and only the owner's Google address may sign in. (A "Sign in with Claude" for third-party
  sites does not exist.)
- **Write tools are on.** `UNIFI_ALLOW_WRITES: "true"`. The existing per-call `confirm` preview stays.
- **The public address is on example.com through Cloudflare.** It is not behind Cloudflare Access (Claude's servers
  cannot pass an Access login).
- **The owner adds the Cloudflare route by hand, once.** Fleet's Cloudflare publishing (main spec §4.7, phase 3) is a
  separate later project that may take this route over.
- Everything else in the sign-in playback stands: consent once per client, 1-hour tokens refreshed in the background,
  a page to disconnect Claude.

## Goals

1. The owner adds a custom connector in Claude with `https://unifi.example.com/mcp`, is sent to Google, signs in, approves
   once, and gets every unifi-mcp tool, including the write tools.
2. The tailnet address (`tailscale serve` on port 7420, `Tailscale-User-Login` allow-list) keeps working as today
   for Claude Code.
3. Any other Google account, an unverified address, or no sign-in at all gets nothing from the public address.
4. Plan Task 12 of the owner-key-store plan is folded in: unifi-mcp reads `UNIFI_API_KEY` from the environment
   (delivered by Fleet `keys:`) and falls back to `config/unifi-api-key` only when the variable is unset or empty.

## Non-goals

- Fleet automating Cloudflare (tunnels, DNS, Access) — phase 3.
- More than one user, roles, or other identity providers.
- Data Manager's `X-Api-Key` path. It is not ported (see anti-goals).

## Anti-goals (failure even if it works)

- Any route from the internet to a tool without a Google sign-in by the allowed address.
- The public address honouring `Tailscale-User-Login`, or any header a caller can set, as identity.
- A long-lived bearer secret (API key, non-expiring token) that works from the internet.
- Secret values in git, logs, MCP output, the SMB share, or chat. Claude never holds Google or Cloudflare credentials.
- A copy of `./data` alone (a backup, Fleet's trash) being enough to mint tokens or forge the sign-in cookie.

## Shape

```
Claude (Anthropic cloud) ─https─► Cloudflare ─tunnel─► cloudflared (sidecar) ─► run/public.sock ─► unifi-mcp
Claude Code (tailnet)  ─https─► tailscale serve :7420 ─────────────────────────► run/mcp.sock    ─► unifi-mcp
```

### Two listeners, two trust models

- unifi-mcp listens on two Unix sockets in `./run`: `mcp.sock` (tailnet, unchanged) and `public.sock` (new).
- Each request is tagged with the listener it arrived on, from the connection's local endpoint. The listener alone
  picks the authentication:
  - `mcp.sock`: the existing `TailscaleServeAuthHandler`, unchanged.
  - `public.sock`: OAuth bearer tokens only. `Tailscale-User-Login` is ignored there, and a test proves a request
    carrying it, with no token, is refused.
- The OAuth, sign-in, consent, connections and icon routes exist only on `public.sock`.
- Both sockets are mode 600, owned by the container user. cloudflared runs as the same user so it can connect.

### Public address

- Base URL from config: `MCP_PUBLIC_BASE_URL=https://unifi.example.com` (hostname is my call; the owner may change it).
  Issuer, audience, metadata URLs and redirect URIs all derive from it.
- On `public.sock`, a request whose `Host` is not that host gets 421 and nothing else. The builder confirms cloudflared
  passes `Host: unifi.example.com` through to the socket before relying on this.
- The caller's IP for logs and rate limits is `CF-Connecting-IP` (only trusted on `public.sock`).

### cloudflared sidecar *(my call)*

- The bundle gains a `cloudflared` service: the official image pinned by digest, `tunnel --no-autoupdate run`, the
  tunnel token from the owner key `UNIFI_MCP_TUNNEL_TOKEN`, read-only root filesystem, no published ports, `./run`
  mounted at the same path as in unifi-mcp.
- The tunnel is remotely managed. The owner creates it in the Cloudflare dashboard with one public hostname,
  `unifi.example.com` → service type UNIX → `/run/unifi-mcp/public.sock`. The builder confirms that path works with the
  dashboard's UNIX service type; if not, fall back to the smallest change that keeps no published port.
- Why a sidecar rather than the NAS's existing cloudflared: no port has to open between hosts, and the app and its
  route deploy and stop together. Cost if wrong: phase 3 moves the route to a per-host cloudflared later.

### Sign-in (port of Data Manager, login swapped to Google)

- Authorization server inside unifi-mcp, as Data Manager:
  - `/.well-known/oauth-protected-resource` (from the MCP SDK's `AddMcp` auth, resource `{base}/mcp`, scope
    `mcp:tools`, `WWW-Authenticate: Bearer resource_metadata=…` on 401).
  - `/.well-known/oauth-authorization-server` (also at `openid-configuration`), `/oauth/jwks`, `/oauth/register`
    (RFC 7591, public clients, 1–5 https redirects, rate-limited), `/oauth/authorize`, `/oauth/consent`,
    `/oauth/token`, `/oauth/revoke` (revokes the whole refresh family).
  - Code flow only, `authorization_code` and `refresh_token` grants, PKCE S256 only, token auth `none`,
    `client_id_metadata_document_supported: true`.
  - CIMD exactly as Data Manager: 64 KB cap, 5 s timeout, no redirects, 1 h cache of 256, public IPs only and connect
    to the resolved IP, self-referential document, redirect rules, `resource` must equal `{base}/mcp` else
    `invalid_target`.
  - Tokens as Data Manager: RS256 JWT `typ=at+jwt`, 1 hour, `iss` = base URL, `aud` = `{base}/mcp`, claims sub, jti,
    email, name, client_id, scope. Codes 256-bit, single use, 5 minutes, stored hashed. Refresh tokens opaque,
    hashed, 30 days, rotated on every use; reuse revokes the family; tombstones 60 days.
- Login: `/oauth/authorize` needs the sign-in cookie; without it, it challenges Google.
  - ASP.NET OpenID Connect to Google (`https://accounts.google.com`), scopes `openid email profile` only, callback
    `{base}/signin-google`, response mode `query` *(my call)*, so the callback is a plain GET and not a cross-site
    form POST from Google.
  - After the callback the email must be in the allow-list and Google's `email_verified` must be true. Otherwise the
    cookie is not issued and the page says "This Google account isn't allowed." in plain words.
  - Allow-list: `config/allowed-google-emails`, one address per line, read on each use like `allowed-logins`.
    The owner's address lives there, not in git.
  - Cookie `unifi.auth`: Secure, HttpOnly, SameSite=Lax, 30-day sliding, 90-day absolute, as Data Manager.
- The allow-list is re-checked on every refresh-token use and every use of the sign-in cookie *(my call)*, so removing
  the address ends access within an hour and closes `/connections` and `/oauth/authorize` at once.
- Refresh lifetime is per token and restarts on each rotation (as Data Manager), so Claude stays signed in while the
  connector is used at least once every 30 days.
- Consent page once per (user, client), with a DataProtection-protected pending token (10 min), naming the client_id
  host for CIMD clients, as Data Manager.
- Connections page `/connections` (cookie sign-in): each connected client, when it was approved and last used, and a
  Disconnect button that revokes its refresh families.
- Icons at `{base}/mcp/icon-512.png`, `icon-180.png`, `icon-32.png`, anonymous, cached 1 day. Fleet's `deploy_app`
  carries text files only, so the icons are embedded resources in the assembly or drawn in code, not bundle files.

### Request guard

`RequestGuard` (refuse any Origin, JSON-only POSTs) stays as is on `mcp.sock` and on `/mcp` of `public.sock`. On the
other public routes the browser pages need their own rule: `GET` pages and the consent `POST` accept an `Origin`
equal to the base URL's origin only; `/oauth/token`, `/oauth/register` and `/oauth/revoke` accept their spec content
types (form-encoded or JSON); `/signin-google` is a GET (response mode `query`). No CORS unless Data Manager has it and
Claude needs it (builder checks).

### Storage

- SQLite at `./data/oauth.db` behind a port of `IMcpOAuthStore`. Code redeem, refresh redeem and family revoke are
  atomic (single conditional `UPDATE` or a transaction). Expired rows are swept at startup and hourly.
- The RSA-2048 signing key is generated on first start and kept in `./data`, protected by DataProtection.
- The DataProtection keyring lives in `./data/keys`, encrypted at rest with a key derived from the Fleet-generated
  secret `UNIFI_MCP_DATA_KEY` (`secrets:` in fleet.yaml). Builder's choice of mechanism (custom `IXmlEncryptor` or
  similar). This is what makes a copy of `./data` alone useless.
- If `UNIFI_MCP_DATA_KEY` is missing or can't decrypt the existing keyring, the public listener refuses everything
  (503) and logs why; the tailnet listener keeps working. Nothing is regenerated over the top of the old keys
  *(my call)*.
- Likewise, if the Google keys or the public base URL are missing, the public listener refuses everything and the
  startup summary names what is missing (names only); the tailnet listener is unaffected.

### Rate limits

ASP.NET rate limiting on `public.sock`, partitioned by `CF-Connecting-IP`, on register, authorize and token. Copy
Data Manager's numbers.

### Logs

Sign-ins, refusals (reason, IP, email if any), client registrations, token issue and revoke, with client id and email.
Never a token, code, cookie, secret or key value.

## Bundle changes, in two deploys *(my call)*

Fleet refuses to deploy an app that declares a key that is unset or not granted to it, so the keys arrive in stages and
a tailnet-only fix is never blocked on the Google or Cloudflare setup.

- **Deploy 1 (Task 12 + writes):** `keys: [UNIFI_API_KEY]`, `UNIFI_ALLOW_WRITES: "true"`, `UNIFI_API_KEY: ${UNIFI_API_KEY}`.
  The new code ships too, with the public listener idle (its keys are absent, so it refuses everything).
- **Deploy 2 (connector):** add `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `UNIFI_MCP_TUNNEL_TOKEN` to `keys:`,
  `secrets: [UNIFI_MCP_DATA_KEY]`, `MCP_PUBLIC_BASE_URL`, `./data:/data`, and the `cloudflared` service. The client ID
  isn't secret, but keeping it as an owner key keeps it out of git *(my call)*. Still no published ports.
- Version 0.2.0.
- Before deploy 1 the builder confirms with `release_status` that fleetd has the `keys:` delivery applied (release
  1ebea1dc).

## Owner steps (the runbook goes in the README)

Before deploy 1:
1. `fleet-key allow UNIFI_API_KEY unifi-mcp`. After deploy 1 shows the key arriving (`getting_started` says
   configured), delete `config/unifi-api-key`.

Before deploy 2:
2. Google Cloud Console: a project, OAuth consent screen External and "In production" (basic scopes need no review), a
   Web OAuth client with redirect URI `https://unifi.example.com/signin-google`. Store both values with `fleet-key set`
   and grant them to unifi-mcp with `fleet-key allow`.
3. Cloudflare Zero Trust: create the tunnel, store its token as `UNIFI_MCP_TUNNEL_TOKEN` and grant it, add the public
   hostname. Check that no Access application (including a wildcard like `*.example.com`), WAF, bot-fight or
   challenge rule applies to that hostname.
4. Put the Google address in `config/allowed-google-emails`.
5. If the tunnel doesn't connect, fleet-mini may be blocked from Cloudflare on outbound port 7844; opening that is a
   UniFi firewall change for the owner to approve.

After deploy 2:
6. Claude Settings → Connectors → Add custom connector → `https://unifi.example.com/mcp`. Sign in, approve, and ask
   Claude to call `getting_started`.

## Tests that must exist

Against a fake Google OIDC provider and the fake UniFi handler; nothing public is needed to run them.

- No token on `/mcp` → 401 with `resource_metadata`. A token with the wrong audience, wrong issuer, wrong key or past
  expiry → 401.
- `Tailscale-User-Login` on `public.sock` without a token → refused; tailnet behaviour on `mcp.sock` unchanged.
- OAuth routes do not exist on `mcp.sock`.
- Wrong Google account → refused page, no cookie; `email_verified` false → refused.
- PKCE mismatch, reused code, expired code → `invalid_grant`. Reused refresh token → family revoked.
- Address removed from the allow-list → next refresh fails, and the existing sign-in cookie no longer opens
  `/connections` or `/oauth/authorize`.
- Data key missing or wrong → public listener 503, tailnet listener still serves.
- Wrong `Host` on `public.sock` → 421.
- Full flow: register, authorize, Google, consent, token, call a tool, refresh, revoke.
- `UNIFI_API_KEY` env wins over the file; the file is used only when the variable is unset or empty.

## Left to the builder

File layout, library choices within .NET 10 and the existing `ModelContextProtocol.AspNetCore` 2.3.1, page styling,
icon artwork, rate-limit numbers (copy Data Manager's), sweep timing, and how the DataProtection keyring is encrypted.
