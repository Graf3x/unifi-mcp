# Security policy

unifi-mcp holds an API key to your network console, so security reports are taken seriously.

## Supported versions

Only the latest release gets fixes. Update with `docker compose pull && docker compose up -d`, after a backup
(README, [Backup and rollback](README.md#backup-and-rollback)).

## Reporting a vulnerability

Please report privately, not in a public issue: open the repository's **Security** tab and choose
**Report a vulnerability**. Include what you found, how to reproduce it, and the version (the image tag or commit you
run). Leave out real API keys, certificates, sign-in codes and network data.

The maintainer replies in that private thread. Once a fix is released, the advisory is published with credit to you,
unless you prefer otherwise.

## In scope

- Anything that lets a caller reach the tools, the console or the API key without being allowed: sign-in, OAuth tokens,
  the access token, the tailnet login check, the loopback mode.
- Anything that sends the API key or console data somewhere other than the caller who asked.
- The setup page: anything that shows it, or the sign-in code, to someone without the link from the server's log, or
  reaches it through the tunnel or the tailnet.
- Certificate checks being skipped or weakened.
- Writes happening without `UNIFI_ALLOW_WRITES=true` and the owner's approval: the confirmation form where the client
  can show one (a write the model approved for itself there is a vulnerability), a matching `confirm` elsewhere.

Weaknesses in UniFi itself belong with Ubiquiti, not here.
