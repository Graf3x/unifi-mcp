#!/usr/bin/env bash
# The image gate: starts one built image the way docker-compose.yml runs it and fails unless it comes up healthy with
# its listeners kept apart. CI runs it on every image it builds, and on a release on the exact pushed digest, on amd64
# and arm64, before any tag points at it (.github/workflows/image.yml).
#
#   tools/image-gate.sh <image> [platform]      e.g. tools/image-gate.sh unifi-mcp:gate linux/arm64
#
# Needs a Linux host with Docker and curl. Every value it gives the server is synthetic, and no API key is set, so the
# server never contacts a console. It runs the container as your own user (as PUID:PGID does), or as 10001:10001 when you
# are root, never as root; GATE_UID and GATE_GID choose another non-root pair.
set -euo pipefail

image=${1:?usage: tools/image-gate.sh <image> [platform]}
platform=${2:-}
uid=${GATE_UID:-$(id -u)}
gid=${GATE_GID:-$(id -g)}
if [ "$uid" = 0 ] && [ -z "${GATE_UID:-}" ]; then uid=10001 gid=10001; fi
if [ "$uid" = 0 ] || [ "$gid" = 0 ]; then echo "gate: refusing to run the image as root" >&2; exit 2; fi
setup_port=${GATE_SETUP_PORT:-15198}
public_host=unifi.example.com
name="unifi-mcp-gate-$$"
work=$(mktemp -d)
data="$work/data"
run="$work/run"
# The public socket has a folder of its own, as in compose: only it is mounted into the tunnel container.
run_public="$work/run-public"
tailnet_sock="$run/mcp.sock"
public_sock="$run_public/public.sock"
failed=0

cleanup() {
  status=$?
  if [ "$status" -ne 0 ] || [ "$failed" -ne 0 ]; then
    echo "---- container log ----" >&2
    docker logs "$name" >&2 2>&1 || true
  fi
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$work" 2>/dev/null || true
}
trap cleanup EXIT

fail() { echo "gate: FAIL: $*" >&2; failed=1; }
pass() { echo "gate: ok: $*"; }

mkdir -m 0700 "$data" "$run" "$run_public"
if [ "$(id -u)" = 0 ]; then chown "$uid:$gid" "$data" "$run" "$run_public"; fi

docker run -d --name "$name" ${platform:+--platform "$platform"} \
  --user "$uid:$gid" --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  -v "$data:/data" -v "$run:/run/unifi-mcp" -v "$run_public:/run/unifi-mcp-public" \
  -p "127.0.0.1:$setup_port:5198" \
  -e UNIFI_URL=https://192.168.1.1 -e UNIFI_SITE=default \
  -e MCP_SIGNIN=code -e MCP_PUBLIC_BASE_URL="https://$public_host" \
  -e UNIFI_MCP_DATA_KEY="$(head -c 32 /dev/urandom | base64)" \
  -e MCP_SETUP_LISTEN=http://0.0.0.0:5198 \
  "$image" >/dev/null

# ---- The artifact ----
if [ -n "$platform" ]; then
  arch=$(docker image inspect -f '{{.Os}}/{{.Architecture}}' "$image")
  [ "$arch" = "$platform" ] && pass "image is $arch" || fail "image is $arch, expected $platform"
fi

# ---- Healthy startup: the image's own HEALTHCHECK, read-only, as the given user ----
deadline=$((SECONDS + ${GATE_TIMEOUT:-150}))
while :; do
  state=$(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$name")
  case $state in
    "running healthy") pass "healthy"; break ;;
    "running none") fail "the image has no HEALTHCHECK"; exit 1 ;;
    "running unhealthy") fail "unhealthy"; exit 1 ;;
    running\ *) ;;
    *) fail "the container stopped ($state)"; exit 1 ;;
  esac
  if [ "$SECONDS" -ge "$deadline" ]; then fail "not healthy in time ($state)"; exit 1; fi
  sleep 2
done

[ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$name")" = true ] && pass "read-only root filesystem" || fail "root filesystem is writable"
pid=$(docker inspect -f '{{.State.Pid}}' "$name")
running_as=$(stat -c '%u:%g' "/proc/$pid")
[ "$running_as" = "$uid:$gid" ] && pass "runs as $running_as" || fail "runs as $running_as, expected $uid:$gid"

# ---- Requests ----
# The status code of one request; 000 when nothing answered.
status() { curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$@" || true; }
expect() {
  local want=$1 what=$2; shift 2
  local got
  got=$(status "$@")
  [ "$got" = "$want" ] && pass "$what -> $got" || fail "$what -> $got, expected $want"
}

setup="http://127.0.0.1:$setup_port"
# The setup listener serves /setup and nothing else, whatever the path.
setup_refuses() { expect 404 "setup listener refuses $1" "$setup$1"; }

# ---- Setup listener isolation ----
published=$(docker port "$name")
if [ "$published" = "5198/tcp -> 127.0.0.1:$setup_port" ]; then pass "only the setup port is published, on loopback"; else fail "published: $published"; fi
setup_refuses /mcp
setup_refuses /healthz
setup_refuses /oauth/jwks
setup_refuses /.well-known/oauth-authorization-server
setup_refuses /connections
setup_refuses /signin-code
expect 401 "setup page without its key" "$setup/setup"
expect 403 "setup page with a wrong key" "$setup/setup?key=not-this-servers-key"
expect 421 "setup page under a host name" -H "Host: $public_host" "$setup/setup"
key=$(docker logs "$name" 2>&1 | grep -o 'setup?key=[A-Za-z0-9_-]*' | tail -n 1 | cut -d= -f2)
if [ -n "$key" ]; then expect 303 "setup page with the key from the log" "$setup/setup?key=$key"; else fail "no setup link in the log"; fi

# ---- The sockets ----
check_sockets() {
  local sock mode
  for sock in "$tailnet_sock" "$public_sock"; do
    if [ ! -S "$sock" ]; then fail "no socket at ${sock#"$work"/}"; continue; fi
    mode=$(stat -c '%a %u' "$sock")
    [ "$mode" = "600 $uid" ] && pass "${sock#"$work"/} is 600, owned by $uid" || fail "${sock#"$work"/} is $mode, expected 600 $uid"
  done
  # The public socket's folder holds only what the tunnel container may reach: never the tailnet socket.
  mode=$(stat -c '%a %u' "$run_public")
  [ "$mode" = "700 $uid" ] && pass "run-public is 700, owned by $uid" || fail "run-public is $mode, expected 700 $uid"
  if [ -e "$run_public/mcp.sock" ]; then fail "the tailnet socket is in run-public"; else pass "the tailnet socket is not in run-public"; fi
}
check_sockets
# The log is captured first: a failed docker logs stops the gate rather than reading as a clean log.
log=$(docker logs "$name" 2>&1)
if grep -qE "Public socket not bound|share the folder" <<< "$log"; then
  fail "the log says the public socket is not bound, or shares the tailnet folder"
else pass "the public socket is bound in a folder of its own"; fi

expect 200 "tailnet socket health" --unix-socket "$tailnet_sock" http://localhost/healthz
expect 404 "tailnet socket refuses the setup page" --unix-socket "$tailnet_sock" http://localhost/setup
expect 404 "tailnet socket refuses OAuth" --unix-socket "$tailnet_sock" http://localhost/oauth/jwks
expect 404 "public socket refuses the setup page" --unix-socket "$public_sock" "http://$public_host/setup"
expect 421 "public socket refuses another host name" --unix-socket "$public_sock" http://localhost/oauth/jwks
# The connector's first key: the keyring and the store are written to /data under a read-only root filesystem.
expect 200 "public socket serves the signing keys" --unix-socket "$public_sock" "http://$public_host/oauth/jwks"
for f in oauth.db keys; do
  [ -e "$data/$f" ] && [ "$(stat -c '%u' "$data/$f")" = "$uid" ] && pass "/data/$f written as $uid" || fail "/data/$f missing or not owned by $uid"
done

if [ "$failed" -ne 0 ]; then echo "gate: $image failed" >&2; exit 1; fi
echo "gate: $image passed"
