#!/usr/bin/env bash
# Checks, on a Linux host with Docker, that a tunnel Compose file keeps the tunnel container away from the tailnet socket:
#   - the public socket answers through the tunnel container's mount;
#   - the tunnel container can neither see nor open run/mcp.sock;
#   - the host's tailnet route (what `tailscale serve` uses) answers, and again after a restart and after a hard kill
#     (which leaves stale sockets behind for the server to clear).
#
# Usage, from anywhere:  tests/compose/socket-isolation.sh [docker-compose.yml | deploy/compose.yaml]
# Needs Docker with Compose v2.24 or later, and curl on the host. The server image is built from this checkout's
# Dockerfile (for deploy/compose.yaml too: this checks its mounts, not the Fleet build). cloudflared is replaced by a
# curl image that keeps the file's user, read-only root and mounts, since only those decide what it can reach. All
# values are synthetic; nothing is sent to Cloudflare or a console.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
file=${1:-docker-compose.yml}
[ -f "$repo/$file" ] || { echo "no such Compose file: $file" >&2; exit 2; }

work=$(mktemp -d "${TMPDIR:-/tmp}/unifi-mcp-sockets.XXXXXX")
project="unifi-mcp-sockets-$$"
host="unifi.example.com"

compose() { docker compose -p "$project" --project-directory "$work" -f "$work/compose.yaml" -f "$work/compose.test.yaml" "$@"; }
cleanup() { compose down --remove-orphans >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; compose logs --no-color unifi-mcp >&2 || true; exit 1; }
pass() { echo "ok: $*"; }

cp "$repo/$file" "$work/compose.yaml"
mkdir -p "$work/data" "$work/run" "$work/run-public" "$work/config"
cat > "$work/.env" <<EOF
PUID=$(id -u)
PGID=$(id -g)
TUNNEL_TOKEN=synthetic-not-a-token
UNIFI_MCP_TUNNEL_TOKEN=synthetic-not-a-token
UNIFI_URL=https://192.168.1.1
MCP_PUBLIC_BASE_URL=https://$host
MCP_SETUP_PAGE=off
EOF
cat > "$work/compose.test.yaml" <<EOF
services:
  unifi-mcp:
    image: unifi-mcp:socket-isolation-test
    build:
      context: $repo
      dockerfile: Dockerfile
    pull_policy: build
    ports: !reset []
  cloudflared:
    image: curlimages/curl:8.11.1
    entrypoint: ["sleep"]
    command: ["infinity"]
EOF

tailnet_from_host() { curl -sf --max-time 5 --unix-socket "$work/run/mcp.sock" http://localhost/healthz 2>/dev/null; }

wait_for_server() {
  for _ in $(seq 1 60); do
    [ "$(tailnet_from_host || true)" = "ok" ] && return 0
    sleep 1
  done
  fail "the tailnet socket did not answer from the host within 60 seconds ($1)"
}

# The server answered on the public socket: 200, or 503 while the connector is not configured (as with these values).
check_public_from_sidecar() {
  local code
  code=$(compose exec -T cloudflared curl -s -o /dev/null -w '%{http_code}' --max-time 5 \
    --unix-socket /run/unifi-mcp-public/public.sock -H "Host: $host" "http://$host/healthz" || true)
  case "$code" in
    200|503) pass "the tunnel container reaches the public socket ($1, HTTP $code)" ;;
    *) fail "the tunnel container got HTTP '$code' from the public socket ($1)" ;;
  esac
}

compose up -d --build
wait_for_server "first start"
pass "the host reaches the tailnet socket"
check_public_from_sidecar "first start"

[ "$(stat -c %a "$work/run/mcp.sock")" = "600" ] || fail "run/mcp.sock is not mode 600"
[ "$(stat -c %a "$work/run-public/public.sock")" = "600" ] || fail "run-public/public.sock is not mode 600"
[ ! -e "$work/run/public.sock" ] || fail "the public socket is in the tailnet folder"
pass "both sockets are mode 600, each in its own folder"

seen=$(compose exec -T cloudflared sh -c \
  'find / \( -path /proc -o -path /sys \) -prune -o -name "mcp.sock*" -print 2>/dev/null; awk "\$5 == \"/run/unifi-mcp\"" /proc/self/mountinfo' || true)
[ -z "$seen" ] || fail "the tunnel container can see the tailnet socket or its folder: $seen"
pass "the tunnel container cannot see the tailnet socket"

for path in /run/unifi-mcp/mcp.sock "$work/run/mcp.sock"; do
  if compose exec -T cloudflared curl -s --max-time 5 --unix-socket "$path" -H "Tailscale-User-Login: you@example.com" \
      http://localhost/healthz >/dev/null 2>&1; then
    fail "the tunnel container opened the tailnet socket at $path"
  fi
done
pass "the tunnel container cannot open the tailnet socket"

compose restart unifi-mcp
wait_for_server "after a restart"
pass "the host reaches the tailnet socket after a restart"
check_public_from_sidecar "after a restart"

compose kill -s SIGKILL unifi-mcp
compose start unifi-mcp
wait_for_server "after a hard kill"
pass "the host reaches the tailnet socket after a hard kill (stale sockets cleared)"
check_public_from_sidecar "after a hard kill"

echo "PASS: $file keeps the tunnel container away from the tailnet socket"
