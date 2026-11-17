# Claude connector: rulings and deferred minors

Built from plan `docs/superpowers/plans/2026-11-15-claude-connector.md` (spec `docs/superpowers/specs/2026-11-12-claude-connector-design.md`).
Live since 2026-11-16 at `https://unifi.example.com/mcp` (v0.2.1): Google sign-in, consent, token issue, tool calls from claude.ai, sign-in survives a restart, tailnet side unchanged.

## Rulings made during the build

Each with what it costs if wrong.

- Ruling: the sweep hosted service class is written and unit-tested in T3 but registered in the host when the connector is wired (T7 ConnectorHosting) - the store has no DI wiring before then - cost if wrong: one registration line moves.
- Ruling: IMcpOAuthStore source has 16 methods (brief said 15) - followed the source, IOAuthStore has 17 with SweepExpiredAsync - cost if wrong: none (superset).
- Ruling: McpHost.Build calls DataKeys.AddProtectedDataProtection when PublicMissing() is empty (needed by the 503 test; brief omits McpHost from Files); store/issuer DI registration stays in T7. Restart_keeps_tokens_valid validates against the new issuer's GetJwksAsync (the /oauth/jwks route is T7) - cost if wrong: test re-pointed in T7.
- Ruling: redirect same-origin check compares host only (reviewer Minor 4) - the global constraint says same-origin, so the port must match too; fixed in the fix round - cost if wrong: a redirect on another port of the same host is refused.
- Ruling: /oauth/authorize (T7) and /connections (T8) don't exist yet - T6 maps a minimal cookie-protected /connections placeholder (fixed 'Signed in' page, challenges Google) that T8 replaces; SignInAsync starts at /connections until T7 repoints it to /oauth/authorize - cost if wrong: harness entry point changes in T7.
- Ruling: refresh grant ignoring `resource` (reviewer Minor 1) conflicts with the global constraint (`resource` != {base}/mcp -> invalid_target) - fix in round 1 - cost if wrong: one extra check.
- Ruling: token-endpoint refusals not logged (reviewer Minor 7) conflicts with the spec's Logs section (refusals with reason and IP) - fix in round 1, no secret values - cost if wrong: extra log lines.
- Ruling: NAS cloudflared rejected (owner chose sidecar) - NAS can't reach the unix socket - cost if wrong: none, plan unchanged. Ruling: cloudflared pinned to 2026.11.0 index digest b94b96ed - cost if wrong: one repin.
- Ruling: kept config/cert-sha256 as fallback, like the API key - cost if wrong: one stale file to delete.

## Deferred minors

Findings graded Minor by reviewers and left for later.

- (Task 2) negative 404/header-strip tests pass for wrong reason until routes exist (revisit in T6/T7 tests)
- (Task 2) no positive RequestGuard unit cases (own-origin form POST, no Origin, text/plain 415, tailnet /oauth 403)
- (Task 2) tailnet 404 list uses exact Equals; RFC 8414 suffixed forms (/.well-known/oauth-authorization-server/mcp) not covered - carry to T7
- (Task 2) UnifiCaller selector sends Unknown to TailscaleServe (unreachable; make it == Tailnet) - carry to T7
- (Task 2) SocketPaths evaluated 3 times against filesystem; compute once
- (Task 2) identical Listen and PublicListen not rejected at startup
- (Task 2) dev TCP PortOf reads first URL only, drops :0
- (Task 2) two-socket test leaks temp folder; Listeners.cs no EOF newline
- (Task 2) public socket bound only if folder exists (warning) - accepted, fails closed
- (Task 3) GetRefreshTokenAsync returns expired rows until sweep (T7 must check ExpiresAt)
- (Task 3) RevokeFamilyAsync INSERT OR IGNORE does not refresh revoked_at
- (Task 3) OAuthSweepService uses wall-clock PeriodicTimer, untested
- (Task 3) no schema user_version / migration marker
- (Task 3) sweep DELETEs not in one transaction
- (Task 3) WAL switch result not checked; INSERT OR IGNORE tombstone keeps stale revoked_at within the pre-sweep window (re-grade at final review)
- (Task 4) unreadable keys dir throws out of Build and stops the tailnet too (re-grade at final: likely Important)
- (Task 4) no AES-GCM associated data
- (Task 4) zeroing gaps (TryOpen plain on parse throw, PKCS8 bytes)
- (Task 4) VerifyPkce does not check verifier charset
- (Task 4) tests miss nbf, nonce uniqueness, Unprotect-under-wrong-key no-write
- (Task 5) redirect test can't catch a redirect-following handler (assert CreateHandler settings)
- (Task 5) ReadBoundedAsync streaming bound untested (no Content-Length case; exactly-64KB case)
- (Task 5) IPv4-compatible ::a.b.c.d, NAT64 64:ff9b::/96, 6to4 2002::/16, 2001:db8::/32 judged public
- (Task 5) loopback redirect match ignores query/userinfo
- (Task 5) client_id URL may name any port
- (Task 5) failed CIMD fetches not cached (rate limits in T7 may cover)
- (Task 6) 90-day fallback to IssuedUtc fails open when the stamp is missing (unreachable today)
- (Task 6) Hosting.Diagnostics cap removes all request logs (consider path-only request logger)
- (Task 6) secret-absence log test runs above Trace level, can't prove the Authentication rule
- (Task 6) no negative id_token tests (wrong key/aud/iss/nonce)
- (Task 6) /signout-oidc, /signout-callback-oidc skips untested
- (Task 6) MCP_ALLOWED_GOOGLE_EMAILS env beats the file (document)
- (Task 7) pending consent token replayable within 10 min (DM parity; sub-bound, Lax, Origin guard)
- (Task 7) DCR has no body/URI size caps (Kestrel 30 MB default)
- (Task 7) DCR returns invalid_redirect_uri for non-redirect metadata problems
- (Task 7) consent INSERT OR REPLACE overwrites FamilyId with two consent pages open
- (Task 7) /oauth/revoke doesn't check token client_id (RFC 7009 2.1)
- (Task 7) IssuerSigningKeyResolver sync-over-async relies on OnMessageReceived pre-warm
- (Task 7) refusal log test covers 5 of the refusal kinds
- (Task 7) AuthorizationCode doesn't record its family (pre-revoke code + verifier binds to the new family)
- (Task 7) refresh path UpsertConsentAsync can write back a revoked family in a narrow race (self-heals)
- (Task 8) concurrent refresh/authorize upsert can resurrect a disconnected consent (make LastUsedAt an UPDATE ... WHERE family_id)
- (Task 8) DeleteConsentAsync ignores family (race with re-consent)
- (Task 8) guarded ALTER untested; concurrent startup on old db could fail
- (Task 8) Short() can split surrogate pairs
- (Task 8) no test for tailnet GET /connections 404
- (Final) CIMD ConnectCallback tries only the first public address
- (Final) allow-list empty at startup = not configured until restart (document in Task 9 README)
- (Final) ClientIp sanitized at source also feeds rate-limit key and RegisteredFrom (harmless)
- (Final) mid-run store failure still 500s public OAuth endpoints (startup covered)
- (Final) data-key mismatch only in Information summary (no Warning)
- (Final) client_name accepted 200, stored 100
- (Final) CIMD redirect URIs not length-capped beyond the 64 KB doc cap; LogText leaves bidi controls

## Still for the owner

- Delete the stale fallbacks `config/unifi-api-key` and `config/cert-sha256` on fleet-mini (the Fleet keys win; the files are unused).
- Try Disconnect on `https://unifi.example.com/connections` in a browser (the one live check not yet done).
