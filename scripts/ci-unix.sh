#!/usr/bin/env bash
set -euo pipefail
export NEXUS_STATE_DIR="$(mktemp -d)"
PWD_ROOT="$PWD"
# Unix-domain transport is the Linux default; leave NEXUS_API_MODE unset to prove it.
unset NEXUS_API_MODE
cleanup() {
  kill "${CORE_PID:-}" 2>/dev/null || true
  rm -rf "$NEXUS_STATE_DIR"
}
trap cleanup EXIT
dotnet out/core/NexusOS.Core.dll > out/unix-core.log 2>&1 &
CORE_PID=$!
ready=0
for i in $(seq 1 60); do
  if [[ -S "$NEXUS_STATE_DIR/nexus.sock" && -f "$NEXUS_STATE_DIR/api-token" ]]; then
    if dotnet out/cli/NexusOS.Cli.dll health > out/unix-health.log 2>&1; then ready=1; break; fi
  fi
  if ! kill -0 "$CORE_PID" 2>/dev/null; then cat out/unix-core.log; exit 1; fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then cat out/unix-core.log out/unix-health.log 2>/dev/null; exit 1; fi
grep -q '"status": "ok"' out/unix-health.log
# TCP listener must not exist in Unix-only mode.
if curl --fail --silent --max-time 2 http://127.0.0.1:5077/health > /dev/null; then
  echo "ERROR: TCP endpoint reachable in Unix-only mode" >&2
  exit 1
fi
test "$(stat -c %a "$NEXUS_STATE_DIR")" = 700
# Core must not depend on its working directory: start a second instance from
# a directory this user cannot traverse and confirm it gets past host startup
# (it then refuses only because the first Core owns the socket).
private="$(mktemp -d)"; mkdir "$private/sub"
( cd "$private/sub" && chmod 000 "$private" && timeout 30 dotnet "$PWD_ROOT/out/core/NexusOS.Core.dll" > "$PWD_ROOT/out/unix-cwd.log" 2>&1 ) || true
chmod 700 "$private"; rm -rf "$private"
if grep -q "DirectoryNotFoundException" out/unix-cwd.log; then cat out/unix-cwd.log; exit 1; fi
# A crash (SIGKILL) leaves the socket file behind; a restart must recover.
kill -9 "$CORE_PID"; wait "$CORE_PID" 2>/dev/null || true
test -S "$NEXUS_STATE_DIR/nexus.sock"
dotnet out/core/NexusOS.Core.dll > out/unix-core2.log 2>&1 &
CORE_PID=$!
for i in $(seq 1 60); do
  if dotnet out/cli/NexusOS.Cli.dll health > out/unix-health.log 2>&1; then break; fi
  if ! kill -0 "$CORE_PID" 2>/dev/null; then cat out/unix-core2.log; exit 1; fi
  sleep 1
done
grep -q '"status": "ok"' out/unix-health.log
# A second Core for the same user must refuse rather than steal the socket.
if timeout 30 dotnet out/core/NexusOS.Core.dll > out/unix-core3.log 2>&1; then
  echo "ERROR: second Core started on a live socket" >&2; exit 1
fi
grep -q "already serving" out/unix-core3.log
echo "Unix-only Core transport smoke checks passed."
