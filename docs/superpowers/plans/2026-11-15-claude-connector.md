# unifi-mcp Claude Connector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make unifi-mcp usable as a custom connector from the owner's Claude subscription at `https://unifi.example.com/mcp`, with Google sign-in limited to the owner, while the tailnet address keeps working.

**Architecture:** A second Unix socket (`public.sock`) carries internet traffic from a cloudflared sidecar. The listener a request arrived on alone decides its trust model: `mcp.sock` keeps the Tailscale header model, `public.sock` accepts only OAuth access tokens issued by an authorization server ported from Data Manager, whose login is Google OIDC with an email allow-list. OAuth state lives in SQLite in `./data`; the DataProtection keyring there is encrypted with a key derived from a Fleet-generated secret.

**Tech Stack:** .NET 10, ASP.NET Core, `ModelContextProtocol.AspNetCore` 2.3.1, `Microsoft.AspNetCore.Authentication.JwtBearer` / `.OpenIdConnect` 10.0.x, `Microsoft.Data.Sqlite` 10.0.x, xunit, `Microsoft.AspNetCore.TestHost`, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/specs/2026-11-12-claude-connector-design.md`

**Port source:** Data Manager, `Viridian-Education/Data-Manager@master`, folder `Viridian.DataManagement.Api/Mcp/` (files `OAuth/IMcpOAuthStore.cs`, `OAuth/McpOAuthModels.cs`, `OAuth/McpTokenIssuer.cs`, `OAuth/ClientMetadataDocuments.cs`, `OAuth/McpOAuthEndpoints.cs`, `McpHostingExtensions.cs`). Fetch with
`gh api "repos/Viridian-Education/Data-Manager/contents/<path>?ref=master" --jq .content | base64 -d`. Reuse is allowed (owner's IP). Port means: keep the logic, rename `Mcp*` types to the names below, drop Cosmos/partition fields, take time from `TimeProvider`.

## Global Constraints

- Public base URL `https://unifi.example.com` (config `MCP_PUBLIC_BASE_URL`); resource/audience `{base}/mcp`; scope `mcp:tools`.
- PKCE S256 only; grants `authorization_code`, `refresh_token`; token endpoint auth `none`; `client_id_metadata_document_supported: true`.
- Access token RS256 JWT `typ=at+jwt`, 1 hour; claims sub, jti, email, name, client_id, scope; `iss` = base URL, `aud` = `{base}/mcp`. RSA-2048.
- Codes 256-bit, single use, 5 minutes, stored hashed. Refresh tokens opaque, stored hashed, 30 days, rotated every use; reuse revokes the family; family tombstones 60 days.
- CIMD: 64 KB cap, 5 s timeout, no redirects, 1 h cache of 256, public IPs only and connect to the resolved IP, self-referential, redirects same-origin with the client_id URL or loopback; loopback redirects match any port; `resource` ≠ `{base}/mcp` → `invalid_target`.
- DCR: public clients, 1–5 https redirect URIs.
- Google: scopes `openid email profile` only, callback `/signin-google`, response mode `query`; email must be in `config/allowed-google-emails` and `email_verified` true.
- Cookie `unifi.auth`: Secure, HttpOnly, SameSite=Lax, 30-day sliding, 90-day absolute. Consent pending token 10 minutes.
- Rate limits on `public.sock`, partition `CF-Connecting-IP`, sliding window, `QueueLimit = 0`: register 10 per 1 h (6 segments); token and revoke 60 per 10 min (5 segments) — Data Manager's values; authorize 30 per 10 min (5 segments) *(Ruling: Data Manager doesn't limit authorize; the spec asks for it)*.
- Icons `{base}/mcp/icon-512.png`, `icon-180.png`, `icon-32.png`, anonymous, `Cache-Control: public, max-age=86400`.
- Never write a token, code, cookie, client secret, tunnel token, data key or API key value to logs, MCP output, page bodies or `ToString()`.
- No published ports. The X-Api-Key scheme from Data Manager is not ported.
- Owner-only actions stay with the owner: every `fleet-key` command, Google Cloud Console, Cloudflare dashboard, deleting files in `config/`, UniFi firewall changes. Claude never sees those values.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The repo has no remote; nothing is pushed.

## Review Focus

1. **A container restart** must keep existing connections: an access token and refresh token issued before a rebuild of the app over the same `./data` and data key still work after it. (Task 4 test `Restart_keeps_tokens_valid`, Task 7 test `Refresh_after_restart_succeeds`.)
2. **Claude's real client shapes**: a CIMD client_id URL with redirect `https://claude.ai/api/mcp/auth_callback`, and a DCR-registered client with the same redirect, both complete the full flow. (Task 7 tests `Full_flow_with_cimd_client`, `Full_flow_with_registered_client`.)
3. **Secret values leaking** through `Settings.ToString()`, the startup summary, or any log line during a full flow. (Task 1 test `Settings_tostring_hides_secret_values`, Task 7 test `Full_flow_logs_hold_no_secret_values`.)
4. **Host header variants**: `UNIFI.example.com` and `unifi.example.com:443` are the public host; `evil.example` is 421. (Task 2 test `Public_host_match_ignores_case_and_default_port`.)
5. **Time limits** measured with `TimeProvider`, not wall clock: a cookie older than 90 days is rejected even if used daily; a refresh token unused for 30 days fails. (Task 6 test `Cookie_older_than_90_days_is_rejected`, Task 7 test `Refresh_token_expires_after_30_days`.)

## File map

- Modify `src/UnifiMcp/Settings.cs` — new public-side settings, env-first API key, secret-safe printing.
- Create `src/UnifiMcp/Listeners.cs` — which socket a request came in on.
- Modify `src/UnifiMcp/CallerAuth.cs` — `RequestGuard` learns the listener and path.
- Modify `src/UnifiMcp/McpHost.cs` — two sockets, the caller-scheme selector, wiring of the connector.
- Create `src/UnifiMcp/Connector/` — `ConnectorState.cs`, `PublicGate.cs`, `OAuthModels.cs`, `IOAuthStore.cs`, `SqliteOAuthStore.cs`, `DataKeys.cs`, `TokenIssuer.cs`, `ClientMetadataDocuments.cs`, `GoogleSignIn.cs`, `OAuthEndpoints.cs`, `Pages.cs`, `ConnectionsPage.cs`, `IconData.cs`, `ConnectorHosting.cs`.
- Tests in `tests/UnifiMcp.Tests/`: `SettingsAndPinTests.cs` (extend), `ListenerTests.cs`, `OAuthStoreTests.cs`, `DataKeyTests.cs`, `TokenIssuerTests.cs`, `ClientMetadataTests.cs`, `GoogleSignInTests.cs`, `OAuthFlowTests.cs`, `ConnectionsTests.cs`, helpers `FakeGoogle.cs`, `PublicHarness.cs`, `CookieJar.cs`, `CapturingLogger.cs`.
- Modify `deploy/compose.yaml`, `deploy/fleet.yaml`, `deploy/SHA256SUMS`, `README.md`, `src/UnifiMcp/UnifiMcp.csproj` (`<Version>0.2.0</Version>`, packages).

Test command for every task: `dotnet test S:\Source\unifi-mcp\unifi-mcp.slnx` (expected: all pass, 0 failed). Single test: add `--filter "FullyQualifiedName~<TestName>"`.

---

### Task 1: Settings — env-first UniFi key and the public side's configuration

**Files:** Modify `src/UnifiMcp/Settings.cs`, `src/UnifiMcp/McpHost.cs` (startup summary); Test `tests/UnifiMcp.Tests/SettingsAndPinTests.cs`.

**Interfaces — Produces:**
- `Settings` new init properties: `ApiKeyValue` (env `UNIFI_API_KEY`), `PublicListen` (env `MCP_PUBLIC_LISTEN`, default `unix:/run/unifi-mcp/public.sock`), `PublicBaseUrl` (`Uri?`, env `MCP_PUBLIC_BASE_URL`; must be `https`, no path other than `/`, no query, else `ArgumentException("MCP_PUBLIC_BASE_URL must be an https:// origin.")`), `GoogleClientId` (env `GOOGLE_CLIENT_ID`), `GoogleClientSecret` (env `GOOGLE_CLIENT_SECRET`), `GoogleAuthority` (`Uri`, default `https://accounts.google.com`, **not** read from env), `AllowedGoogleEmailsFile` (env `MCP_ALLOWED_GOOGLE_EMAILS_FILE`, default `/config/allowed-google-emails`), `AllowedGoogleEmailsValue` (env `MCP_ALLOWED_GOOGLE_EMAILS`, for dev), `DataDir` (env `MCP_DATA_DIR`, default `/data`), `DataKey` (env `UNIFI_MCP_DATA_KEY`).
- `string? ApiKey()` → `ApiKeyValue` if non-blank, else the file.
- `IReadOnlyList<string> AllowedGoogleEmails()` — same parsing as `AllowedLogins()`.
- `IReadOnlyList<string> PublicMissing()` — names only, in this order: `MCP_PUBLIC_BASE_URL`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `UNIFI_MCP_DATA_KEY` (also listed when shorter than 16 characters), `allowed Google emails (<file>)` when the list is empty.
- `Settings.ToString()` prints `***` for `ApiKeyValue`, `GoogleClientSecret`, `DataKey` (override `PrintMembers`).
- `McpHost.StartupSummary` gains `, connector <ready|not configured (names)>` using `PublicMissing()`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Env_api_key_wins_over_the_file()                // file "FILE-KEY", env UNIFI_API_KEY "ENV-KEY" → ApiKey() == "ENV-KEY"
[Fact] public void Blank_env_api_key_falls_back_to_the_file()      // env "  " → ApiKey() == "FILE-KEY"
[Fact] public void Public_base_url_must_be_an_https_origin()       // "http://x", "https://x/path", "https://x?q" → ArgumentException; "https://unifi.example.com" ok
[Fact] public void Public_missing_names_every_absent_value()       // empty settings → exactly the five entries above, in order
[Fact] public void Short_data_key_counts_as_missing()              // DataKey "short" → contains "UNIFI_MCP_DATA_KEY"
[Fact] public void Settings_tostring_hides_secret_values()         // ToString() contains none of the three values, contains "***"
[Fact] public void Startup_summary_never_contains_connector_secrets() // StartupSummary(...) contains none of the values
```

- [ ] **Step 2: Run them — expected FAIL (members don't exist).**
- [ ] **Step 3: Implement in `Settings.cs` and `McpHost.StartupSummary`.**
- [ ] **Step 4: Run the full suite — expected all pass.**
- [ ] **Step 5: Commit** `feat(settings): env-first UniFi key and connector settings`.

### Task 2: Two listeners and the public gate

**Files:** Create `src/UnifiMcp/Listeners.cs`, `src/UnifiMcp/Connector/ConnectorState.cs`, `src/UnifiMcp/Connector/PublicGate.cs`; Modify `src/UnifiMcp/McpHost.cs`, `src/UnifiMcp/CallerAuth.cs`, `tests/UnifiMcp.Tests/Harness.cs`; Create `tests/UnifiMcp.Tests/ListenerTests.cs`, `tests/UnifiMcp.Tests/PublicHarness.cs`.

**Interfaces — Produces:**
- `public enum Listener { Tailnet, Public, Unknown }`.
- `public static class Listeners { public static Listener Of(EndPoint? local, Settings s); public static Listener ListenerOf(this HttpContext ctx); }` — `Of`: a `UnixDomainSocketEndPoint` whose path equals the socket path of `s.Listen` → Tailnet, of `s.PublicListen` → Public; an `IPEndPoint` whose port equals the port of a TCP `Listen` / `PublicListen` URL → that listener; anything else, including `null`, → `Unknown`. The middleware stores the result in a request feature that `ListenerOf` reads.
- `McpHost.Build(Settings settings, HttpMessageHandler? unifi = null, Action<WebApplicationBuilder>? configure = null, Func<HttpContext, Listener>? listenerOf = null, HttpMessageHandler? google = null)` — `listenerOf` replaces `Listeners.Of` (tests only; TestServer has no endpoint); `google` is the OIDC backchannel (Task 6).
- `public sealed class ConnectorState { bool Ready; string? Reason; void NotConfigured(IReadOnlyList<string> missing); void Broken(string reason); }` singleton; `Ready` only when neither was called.
- `PublicGate` middleware, in order:
  - `Unknown` → 403.
  - `Public`: `Host` (case-insensitive, `:443` allowed) ≠ `PublicBaseUrl` host → 421; remove `Tailscale-User-Login` from the request; `!ConnectorState.Ready` → 503 body `The connector isn't set up yet.`; path not one of `/mcp`, `/mcp/icon-*.png`, `/.well-known/*`, `/oauth/*`, `/signin-google`, `/connections*`, `/healthz` → 404.
  - `Tailnet`: any of `/oauth/*`, `/signin-google`, `/connections*`, `/.well-known/oauth-authorization-server`, `/.well-known/openid-configuration` → 404.
- `RequestGuard.Check(Listener listener, string path, string method, string? origin, string? contentType, string? publicOrigin)` — Tailnet and Public `/mcp`: today's rules unchanged. Public, other paths: `Origin` must be absent or equal `publicOrigin`; POST content type `application/json` or `application/x-www-form-urlencoded`.
- Kestrel listens on both sockets when both are `unix:`; `RestrictSocket` sets mode 600 on both.
- `McpHost.Build` registers `ConnectorState` and calls `NotConfigured(settings.PublicMissing())` when that list is non-empty (the only place that does).
- `Harness.StartAsync` passes `listenerOf: _ => Listener.Tailnet`. `PublicHarness.StartAsync(bool configured = true, ...)` passes `_ => Listener.Public`, sets `Host` to `unifi.example.com` on its client, and when `configured` fills every `PublicMissing()` value (base `https://unifi.example.com`, Google id/secret test strings, a 32-character data key, `DataDir` in its temp folder, allow-list `PublicHarness.GoogleEmail = "owner@example.test"`). Later tasks extend it.

- [ ] **Step 1: Write the failing tests** (`ListenerTests.cs`)

```csharp
[Fact] public void Unix_paths_map_to_their_listener()             // Of(new UnixDomainSocketEndPoint("/run/unifi-mcp/public.sock"), s) == Public; mcp.sock == Tailnet; "/x.sock" == Unknown
[Fact] public void Null_endpoint_is_unknown()                       // Of(null, s) == Unknown
[Fact] public async Task Unknown_listener_gets_403()                // harness with listenerOf _ => Unknown: GET /healthz → 403
[Fact] public async Task Tailscale_header_on_public_without_token_is_refused() // ready public harness, header = Harness.Login, POST /mcp initialize → 401, never 200
[Fact] public async Task Oauth_routes_do_not_exist_on_the_tailnet()  // tailnet: GET /oauth/authorize, /.well-known/oauth-authorization-server, /connections → 404
[Fact] public async Task Wrong_host_on_public_gets_421()            // Host evil.example → 421
[Fact] public async Task Public_host_match_ignores_case_and_default_port() // "UNIFI.example.com", "unifi.example.com:443" → not 421
[Fact] public async Task Public_answers_503_until_configured()      // public harness with PublicMissing() non-empty → GET /mcp 503; tailnet harness still serves tools
[Fact] public async Task Public_post_from_foreign_origin_is_refused() // POST /oauth/token, Origin https://evil.example → 403
```

  `Tailscale_header_on_public_without_token_is_refused` only needs 401 from the challenge. Until Task 7 wires the bearer scheme, the selector's Public branch returns `AuthenticateResult.NoResult()`, and the challenge returns 401.

- [ ] **Step 2: Run — expected FAIL.**
- [ ] **Step 3: Implement.** Authorization policy `unifi` uses a policy scheme `UnifiCaller` whose `ForwardDefaultSelector` returns `TailscaleServeAuthHandler.SchemeName` for Tailnet and the MCP bearer scheme for Public (registered in Task 7; until then a placeholder that returns no result and challenges 401).
- [ ] **Step 4: Run the full suite — expected all pass, existing tailnet tests unchanged.**
- [ ] **Step 5: Commit** `feat: public socket with its own gate; tailnet unchanged`.

### Task 3: SQLite OAuth store

**Files:** Create `src/UnifiMcp/Connector/OAuthModels.cs`, `IOAuthStore.cs`, `SqliteOAuthStore.cs`; Modify `UnifiMcp.csproj` (add `Microsoft.Data.Sqlite`); Test `tests/UnifiMcp.Tests/OAuthStoreTests.cs`.

**Interfaces — Produces:**
- Records ported from `McpOAuthModels.cs` without `Id`/`PartitionKey`/`Ttl`: `OAuthClient`, `AuthorizationCode`, `RefreshToken`, `Consent`, `SigningKey`, `OAuthUser { Subject, Email, Name }` (no Roles).
- `IOAuthStore`: the 15 methods of Data Manager's `IMcpOAuthStore` under the same names with these types, plus `Task<int> SweepExpiredAsync(CancellationToken ct = default)`.
- `SqliteOAuthStore(string databasePath, TimeProvider time)`. Creates the schema if missing. Codes and refresh tokens are keyed by their hash. `RedeemCodeAsync`, `TryMarkRefreshTokenRedeemedAsync` and `RevokeFamilyAsync` are each a single conditional `UPDATE … RETURNING` or one transaction. Family tombstones are kept 60 days. Sweep removes expired codes, expired refresh tokens and tombstones older than 60 days. A hosted service runs the sweep at startup and every hour.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Code_redeems_once()                        // second RedeemCodeAsync → null
[Fact] public async Task Expired_code_does_not_redeem()             // FakeTimeProvider +6 min → null
[Fact] public async Task Parallel_redeem_has_one_winner()           // 20 parallel RedeemCodeAsync → exactly 1 non-null
[Fact] public async Task Refresh_mark_redeemed_once()               // TryMark… true then false; 20 parallel → exactly one true
[Fact] public async Task Revoked_family_stays_revoked_for_60_days() // IsFamilyRevoked true at +59 d, sweep at +61 d removes the tombstone
[Fact] public async Task Signing_key_first_writer_wins()            // two SaveSigningKeyIfAbsentAsync → both return the first
[Fact] public async Task Store_survives_reopen()                    // new SqliteOAuthStore over the same file reads the client and consent back
[Fact] public async Task Nothing_is_stored_in_the_clear()           // save code/refresh with hash of "SECRET-VALUE"; raw db bytes don't contain "SECRET-VALUE"
```

- [ ] **Step 2: Run — expected FAIL.** **Step 3: Implement.** **Step 4: Full suite passes.**
- [ ] **Step 5: Commit** `feat(connector): SQLite OAuth store with atomic redeem`.

### Task 4: Data keys, keyring encryption and the token issuer

**Files:** Create `src/UnifiMcp/Connector/DataKeys.cs`, `TokenIssuer.cs`; Test `tests/UnifiMcp.Tests/DataKeyTests.cs`, `TokenIssuerTests.cs`.

**Interfaces — Produces:**
- `public static class DataKeys`:
  - `byte[] Derive(string dataKey)`: HKDF-SHA256 over UTF-8 bytes, info `unifi-mcp keyring v1`, 32 bytes.
  - `(bool Ok, string? Reason) Check(string keysDir, byte[] key)`: tries to decrypt every `key-*.xml` in the directory. It reports a mismatch as the reason `UNIFI_MCP_DATA_KEY does not match <dir>`.
  - `void AddProtectedDataProtection(IServiceCollection services, Settings s, ConnectorState state)`:
    - `SetApplicationName("unifi-mcp")`, `PersistKeysToFileSystem(<DataDir>/keys)`.
    - An AES-256-GCM `IXmlEncryptor` / `IXmlDecryptor` pair using the derived key. The decryptor gets the key from DI.
    - When `Check` fails, it calls `state.Broken(reason)` and `DisableAutomaticKeyGeneration()`.
- `public interface ITokenIssuer` = Data Manager's `IMcpTokenIssuer` (Issuer, Audience, lifetimes, `GetSigningKeyAsync`, `IssueAccessTokenAsync(OAuthUser, clientId, scope)`, `GetJwksAsync`). `TokenIssuer(IOAuthStore, IDataProtectionProvider, string issuer, string audience, TimeProvider)`. Static `Hash`, `NewSecret`, `VerifyPkce` as Data Manager.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Keyring_files_hold_no_plain_key_material()       // after one Protect, key-*.xml has no <masterKey>…<value> plaintext, only the encrypted element
[Fact] public void Wrong_data_key_is_detected_without_rewriting()   // protect with key A; Check with key B → Ok false; key files byte-identical afterwards
[Fact] public async Task Wrong_data_key_gives_503_on_public_and_tailnet_still_serves() // PublicHarness over A's DataDir with key B → /mcp 503; Harness tools/list works
[Fact] public async Task Issued_token_has_the_pinned_shape()        // decode: alg RS256, typ at+jwt, iss base, aud base+"/mcp", exp-iat == 3600, claims sub,jti,email,name,client_id,scope
[Fact] public async Task Restart_keeps_tokens_valid()               // issue with app 1, dispose, new app over same DataDir+key validates it against /oauth/jwks key
[Fact] public void Pkce_verifies_s256_only()                        // RFC 7636 appendix B vector true; plain verifier false
```

- [ ] **Step 2: Run — FAIL.** **Step 3: Implement.** **Step 4: Full suite passes.**
- [ ] **Step 5: Commit** `feat(connector): encrypted keyring and token issuer`.

### Task 5: Client metadata documents (CIMD)

**Files:** Create `src/UnifiMcp/Connector/ClientMetadataDocuments.cs` (port verbatim apart from names and `TimeProvider` for the cache); Test `tests/UnifiMcp.Tests/ClientMetadataTests.cs`.

**Interfaces — Produces:** `IClientMetadataDocuments.ResolveAsync(string clientIdUrl, CancellationToken)` → `(ClientMetadataDocument? Document, string? Problem)`; `HttpClientMetadataDocuments` with `HttpClientName`, `CreateHandler()`, and internal `IsPublicAddress`, `IsClientIdUrl`, `Validate`, `RedirectAllowed(IEnumerable<string> registered, string requested)`. If Data Manager has tests for this file (`gh api` its test project), port them as well.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] public void Non_public_addresses_are_refused(string ip)    // 10.0.0.1, 192.168.1.1, 127.0.0.1, 169.254.169.254, ::1, fc00::1, 224.0.0.1 → false; 8.8.8.8 → true
[Fact] public void Document_must_name_itself()                      // client_id in doc ≠ URL → problem
[Fact] public void Redirects_must_share_origin_or_be_loopback()     // https://claude.ai/api/mcp/auth_callback ok for client_id https://claude.ai/...; https://evil.example/cb refused; http://127.0.0.1:1234/cb ok
[Fact] public void Loopback_redirect_matches_any_port()             // registered http://127.0.0.1/cb, requested http://127.0.0.1:5555/cb → allowed
[Fact] public async Task Oversized_document_is_refused()            // 65 KB body → problem
[Fact] public async Task Redirecting_document_is_refused()          // 302 → problem
```

- [ ] **Step 2–4: FAIL, implement, full suite passes.** **Step 5: Commit** `feat(connector): client metadata documents`.

### Task 6: Google sign-in and the cookie

**Files:** Create `src/UnifiMcp/Connector/GoogleSignIn.cs`, `src/UnifiMcp/Connector/Pages.cs` (shared HTML shell and `ErrorPage(title, text)`); Test `tests/UnifiMcp.Tests/GoogleSignInTests.cs`, helpers `FakeGoogle.cs`, `CookieJar.cs`.

**Interfaces — Produces:**
- `public static class GoogleSignIn`:
  - `const string CookieScheme = "unifi.auth"; const string GoogleScheme = "google";`
  - `enum Admission { Allowed, NoEmail, Unverified, NotAllowed }` and `Admission Admit(ClaimsPrincipal p, IReadOnlyCollection<string> allowed)`. It checks the `email` claim, `email_verified == "true"`, and list membership, ignoring case.
  - `void Add(AuthenticationBuilder b, Settings s, HttpMessageHandler? backchannel)`.
- **OIDC settings:** `Authority = s.GoogleAuthority`, `ResponseType = code`, `ResponseMode = query`, `UsePkce = true`, scopes `openid email profile`, `CallbackPath = /signin-google`, `MapInboundClaims = false`, `GetClaimsFromUserInfoEndpoint = false`, `SaveTokens = false`, `SignInScheme = CookieScheme`, and `BackchannelHttpHandler = backchannel` when not null.
- **`OnTokenValidated`:** a refusal writes `ErrorPage("This Google account isn't allowed", …)` with status 403 and calls `HandleResponse()`, so no cookie is issued. Every refusal and every sign-in is logged with its reason and email.
- **Cookie settings:** values from Global Constraints, a `/signin-google` login path, and no automatic redirect for API paths.
- **`OnValidatePrincipal`:** rejects the principal and signs out when `Admit` fails against the current list, or when the ticket's `IssuedUtc` is more than 90 days old, measured by `TimeProvider`.
- **Principal claims:** `sub` (Google `sub`), `email` and `name`.
- **`FakeGoogle : HttpMessageHandler`:**
  - Serves `/.well-known/openid-configuration`, `/jwks` and `/token` for authority `https://google.test`.
  - `Approve(string email, bool verified)` sets the identity the next `/token` returns as an RS256 id_token, with `aud` = client id and the nonce taken from the authorize URL.
  - `CookieJar` is a `DelegatingHandler` that keeps cookies across TestServer requests.
- **`PublicHarness` gains:** `SignInAsync(string email, bool verified = true)`, which drives `/oauth/authorize` → Google → `/signin-google`, and `TimeProvider` (a `FakeTimeProvider`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Admit_requires_verified_listed_email()           // table: listed+verified Allowed; unverified Unverified; other NotAllowed; no email NoEmail; "OWNER@Example.Test" Allowed
[Fact] public async Task Allowed_account_gets_the_cookie()          // SignInAsync(PublicHarness.GoogleEmail) → jar has unifi.auth, Secure, HttpOnly, SameSite=Lax
[Fact] public async Task Other_account_gets_refused_page_and_no_cookie() // 403, body contains "isn't allowed", no unifi.auth
[Fact] public async Task Unverified_email_is_refused()              // verified:false → 403, no cookie
[Fact] public async Task Callback_is_a_get_not_a_form_post()        // Google redirect URL has response_mode=query; /signin-google is reached by GET
[Fact] public async Task Removed_address_loses_the_cookie_at_once() // sign in, empty the allow-list file, GET /connections → not 200, cookie cleared
[Fact] public async Task Cookie_older_than_90_days_is_rejected()    // sign in, advance 1 day at a time, using it each day; on day 91 rejected
```

- [ ] **Step 2–4: FAIL, implement, full suite passes.** **Step 5: Commit** `feat(connector): Google sign-in limited to the allow-list`.

### Task 7: OAuth endpoints, bearer validation and the full flow

**Files:** Create `src/UnifiMcp/Connector/OAuthEndpoints.cs` (port of `McpOAuthEndpoints.cs` except connections), `src/UnifiMcp/Connector/ConnectorHosting.cs`; Modify `McpHost.cs`; Test `tests/UnifiMcp.Tests/OAuthFlowTests.cs`, helper `CapturingLogger.cs`.

**Interfaces:**
- Consumes everything from Tasks 2–6.
- **Produces `ConnectorHosting.AddConnector(WebApplicationBuilder b, Settings s, HttpMessageHandler? google)`:**
  - Registers the store at `<DataDir>/oauth.db`, the issuer, CIMD, `TimeProvider.System` (via `TryAdd`, so tests can replace it), and DataProtection (Task 4).
  - Registers Google (Task 6) and the rate limiter with the policies from Global Constraints. The partition key is `CF-Connecting-IP`, falling back to `unknown`.
  - Registers JwtBearer scheme `McpJwt`, which validates against the issuer's key with `ValidTypes = ["at+jwt"]`, issuer, audience and lifetime, and `ClockSkew` of 2 minutes.
  - Registers `.AddMcp(o => { o.ForwardAuthenticate = "McpJwt"; o.ResourceMetadata = { Resource = base+"/mcp", ResourceName = "UniFi Network", AuthorizationServers = [base], ScopesSupported = ["mcp:tools"], BearerMethodsSupported = ["header"] } })`.
  - When `PublicMissing()` isn't empty it registers nothing that needs those values (Task 2 already marked the state).
- **Produces `ConnectorHosting.MapConnector(WebApplication app)`:** maps the routes listed in the spec. The `UnifiCaller` selector's Public branch now forwards to `McpAuthenticationDefaults.AuthenticationScheme`.
- **Port changes:**
  - Replace the role check with `GoogleSignIn.Admit` against the current list, on authorize, on consent and on every `refresh_token` grant. On refresh, a refusal returns `invalid_grant` and revokes the family.
  - Error and consent pages use `Pages.cs` and say "UniFi Network" where Data Manager says "Data Manager".
- `ServerInfo` gains `WebsiteUrl = base` and the three icons (Task 8 serves them).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task No_token_gets_401_with_resource_metadata()  // WWW-Authenticate contains resource_metadata="https://unifi.example.com/.well-known/oauth-protected-resource…"
[Fact] public async Task Metadata_pins_the_supported_flows()         // AS metadata: S256 only, grants exactly [authorization_code, refresh_token], auth methods [none], cimd true
[Theory] public async Task Bad_tokens_get_401(string kind)           // wrong-audience, wrong-issuer, other-key, expired (FakeTimeProvider +61 min)
[Fact] public async Task Full_flow_with_registered_client()          // register (redirect https://claude.ai/api/mcp/auth_callback) → authorize → Google → consent allow → token → tools/list includes a write tool → refresh → revoke → refresh fails
[Fact] public async Task Full_flow_with_cimd_client()                // client_id https://claude.ai/oauth/mcp-oauth-client-metadata served by a fake metadata handler; same flow
[Fact] public async Task Second_authorize_skips_consent()            // standing consent → redirect with code, no page
[Fact] public async Task Pkce_mismatch_is_invalid_grant()
[Fact] public async Task Reused_code_is_invalid_grant()
[Fact] public async Task Expired_code_is_invalid_grant()             // +6 min
[Fact] public async Task Reused_refresh_token_revokes_the_family()   // old token again → invalid_grant; the newer one now fails too
[Fact] public async Task Removed_address_fails_the_next_refresh()    // empty allow-list → invalid_grant, family revoked
[Fact] public async Task Refresh_token_expires_after_30_days()       // +30 d +1 min → invalid_grant
[Fact] public async Task Refresh_after_restart_succeeds()            // dispose app, rebuild over same DataDir/key, refresh works
[Fact] public async Task Wrong_resource_is_invalid_target()
[Fact] public async Task Register_is_rate_limited_per_client_ip()    // 11th register from one CF-Connecting-IP in an hour → 429; another IP → 201
[Fact] public async Task Full_flow_logs_hold_no_secret_values()      // CapturingLogger: no code, access token, refresh token, client secret, data key, UniFi key, cookie value
```

- [ ] **Step 2–4: FAIL, implement, full suite passes.** **Step 5: Commit** `feat(connector): OAuth server ported from Data Manager`.

### Task 8: Connections page and icons

**Files:** Create `src/UnifiMcp/Connector/ConnectionsPage.cs`, `src/UnifiMcp/Connector/IconData.cs`, plus the icon generator script under `tools/` (artwork is the builder's choice); Modify `ConnectorHosting.MapConnector`; Test `tests/UnifiMcp.Tests/ConnectionsTests.cs`.

**Interfaces — Produces:**
- `GET /connections`: needs a cookie; if there is none, it challenges Google with a return to `/connections`. It renders one row per consent showing the client name, the client_id host, approved-at and last-used, and a Disconnect form.
- `POST /connections/{clientId}/disconnect`: needs a cookie and a same-origin `Origin`. It deletes the consent, revokes its family, logs the email and client id, and redirects to `/connections`.
- `IconData`: base64 PNG constants for 512, 180 and 32 px. Fleet's `deploy_app` can't carry binary files, so the icons aren't stored as resource files.
- `GET /mcp/icon-{512|180|32}.png`: anonymous, served as `image/png` with the cache header.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Connections_lists_the_connected_client()
[Fact] public async Task Disconnect_revokes_its_tokens()             // after disconnect, refresh → invalid_grant; access token still valid until expiry (accepted)
[Fact] public async Task Disconnect_needs_same_origin()              // Origin https://evil.example → 403
[Fact] public async Task Connections_without_cookie_goes_to_google() // 302 to https://google.test/…
[Fact] public async Task Icons_are_anonymous_pngs()                  // 200, image/png, PNG signature bytes, max-age=86400
```

- [ ] **Step 2–4: FAIL, implement, full suite passes.** **Step 5: Commit** `feat(connector): connections page and icons`.

### Task 9: Deploy 1 — Task 12 and write tools (tailnet only)

**Files:** Modify `deploy/compose.yaml`, `deploy/fleet.yaml`, `deploy/SHA256SUMS`, `src/UnifiMcp/UnifiMcp.csproj` (`<Version>0.2.0</Version>`), `README.md`.

- [ ] **Step 1: Bundle for stage 1.**
  - `fleet.yaml` gains `keys: [UNIFI_API_KEY]`.
  - `compose.yaml` image tag becomes `unifi-mcp:0.2.0`, with `UNIFI_ALLOW_WRITES: "true"` and `UNIFI_API_KEY: ${UNIFI_API_KEY}`. Update the header comment.
  - Nothing else changes yet. `MCP_PUBLIC_BASE_URL` stays unset, so the public socket answers 503.
- [ ] **Step 2: Regenerate `deploy/SHA256SUMS`.** It covers every file under `src/UnifiMcp/` (all `.cs` and `UnifiMcp.csproj`). Run `sha256sum` from the repo root and check it with `sha256sum -c deploy/SHA256SUMS`. Expected: every line OK.
- [ ] **Step 3: README.** Rewrite the owner setup as the two-stage runbook from the spec's "Owner steps". Also document `allowed-google-emails` and the connector URL. Use placeholders such as `you@example.com` for addresses and keep real values out. Commit `chore(deploy): stage 1 bundle, v0.2.0, runbook`.
- [ ] **Step 4: Preconditions.** `mcp__fleet__release_status` must show release 1ebea1dc (owner keys) applied. If it doesn't, stop and tell the owner. Then ask the owner to run `fleet-key allow UNIFI_API_KEY unifi-mcp` and wait for their "done".
- [ ] **Step 5: Deploy.** Use `mcp__fleet__deploy_app` for `unifi-mcp` with the bundle files: `fleet.yaml`, `compose.yaml`, and `build/` containing `Dockerfile`, `SHA256SUMS` and every listed source file, laid out as in the first deploy. Then check `mcp__fleet__app_status`, which should show running. `mcp__fleet__app_logs` should show the startup line with `configured, writes ON` and `connector not configured (…)`.
- [ ] **Step 6: Key cut-over.** Ask the owner to delete `/srv/fleet/apps/unifi-mcp/config/unifi-api-key`. Then run `mcp__fleet__restart_app` and confirm the logs still say `configured`. Stage 1 is done.

### Task 10: Deploy 2 — the connector

**Files:** Modify `deploy/compose.yaml`, `deploy/fleet.yaml`, `README.md` (any corrections found on the way).

- [ ] **Step 1: Owner setup.** Give the owner the runbook's steps 2–4: the Google OAuth client and its two keys, the Cloudflare tunnel and its token key, the public hostname, the check that no Access, WAF or bot rule covers it, and `allowed-google-emails`. Wait for "done".
- [ ] **Step 2: Bundle for stage 2.**
  - `fleet.yaml`: `keys: [UNIFI_API_KEY, GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET, UNIFI_MCP_TUNNEL_TOKEN]`, `secrets: [UNIFI_MCP_DATA_KEY]`.
  - `compose.yaml`:
    - `MCP_PUBLIC_BASE_URL: https://unifi.example.com` and the four `${NAME}` variables.
    - `./data:/data`.
    - A `cloudflared` service: `cloudflare/cloudflared@sha256:<digest of the current release>`, `command: tunnel --no-autoupdate run`, `TUNNEL_TOKEN: ${UNIFI_MCP_TUNNEL_TOKEN}`, `user: "${PUID}:${PGID}"`, `read_only: true`, `restart: unless-stopped`, `./run:/run/unifi-mcp`, and no ports.
  - Commit `chore(deploy): stage 2 bundle with cloudflared`.
- [ ] **Step 3: Deploy and check.**
  - Run `mcp__fleet__deploy_app`. `app_status` should show both services running. The unifi-mcp log should show `connector ready`, and the cloudflared log should show a registered connection.
  - If the tunnel can't connect, look for an outbound block on port 7844 and ask the owner to approve the UniFi change. Don't make it yourself.
- [ ] **Step 4: Public checks from here.**
  - `curl -s https://unifi.example.com/.well-known/oauth-protected-resource` → JSON whose `resource` is `https://unifi.example.com/mcp`.
  - `curl -si -X POST -H 'Content-Type: application/json' -d '{}' https://unifi.example.com/mcp` → 401 with `resource_metadata`.
  - `curl -si -H 'Tailscale-User-Login: x' …/mcp` → still 401.
  - If Cloudflare answers with a challenge page instead, return to the owner's Cloudflare check.
  - A 421 means cloudflared isn't passing `Host: unifi.example.com`: the owner sets the route's HTTP host header in the dashboard; don't loosen the check.
- [ ] **Step 5: The owner connects.** They add the custom connector `https://unifi.example.com/mcp` in Claude, sign in with Google, approve, and ask Claude to call `getting_started`. Confirm in `app_logs` that the sign-in, the client registration and token issue are logged without values.
- [ ] **Step 6: Tailnet regression.** A tailnet tool call (Claude Code `unifi` server) still works. Done.
