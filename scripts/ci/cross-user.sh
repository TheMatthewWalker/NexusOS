#!/usr/bin/env bash
# M2-2 (#51): cross-user isolation between two real Linux accounts.
#
# Creates two disposable users, runs an independent Nexus Core for each on
# the default Unix socket, and proves that neither can reach the other's
# socket, token, state directory, SQLite history or conversations. A second
# phase deliberately loosens the filesystem permissions and gives user B a
# copy of A's bearer token, proving the kernel SO_PEERCRED check still
# rejects B before any HTTP processing. Requires root (re-executes via sudo).
# Expects published Core/CLI in out/core and out/cli, and python3.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then exec sudo -E env "PATH=$PATH" bash "$0" "$@"; fi

A=nexus-xuser-a
B=nexus-xuser-b
BIN="$(mktemp -d /tmp/nexus-xuser-bin.XXXXXX)"
DOTNET="$(readlink -f "$(command -v dotnet)")"
PIDS=()
remove_user() {
  # runuser forks, so stop the user's processes directly rather than by PID.
  pkill -u "$1" 2>/dev/null || true
  sleep 1
  pkill -9 -u "$1" 2>/dev/null || true
  userdel -r "$1" 2>/dev/null || true
}
cleanup() {
  for pid in "${PIDS[@]}"; do kill "$pid" 2>/dev/null || true; done
  remove_user "$A"
  remove_user "$B"
  wait 2>/dev/null || true
  rm -rf "$BIN"
}
trap cleanup EXIT

fail() { echo "CROSS-USER FAIL: $*" >&2; exit 1; }
pass() { echo "CROSS-USER PASS: $*"; }

for user in "$A" "$B"; do
  if id "$user" > /dev/null 2>&1; then remove_user "$user"; fi
  useradd --create-home --shell /bin/bash "$user"
done
cp -r out/core out/cli scripts/mock-ollama.py "$BIN/"
chmod -R a+rX "$BIN"
chmod 0755 "$BIN"

as() { local user="$1"; shift; runuser -u "$user" -- env -i HOME="/home/$user" PATH=/usr/bin:/bin "$@"; }
state_of() { echo "/home/$1/.local/state/nexusos"; }
cli() { local user="$1"; shift; as "$user" "$DOTNET" "$BIN/cli/NexusOS.Cli.dll" "$@"; }

python3 "$BIN/mock-ollama.py" > /dev/null 2>&1 &
PIDS+=($!)

start_core() {
  local user="$1"
  as "$user" NEXUS_OLLAMA_URL=http://127.0.0.1:11435 "$DOTNET" "$BIN/core/NexusOS.Core.dll" > "$BIN/$user-core.log" 2>&1 &
  PIDS+=($!)
  for _ in $(seq 1 60); do
    if [[ -S "$(state_of "$user")/nexus.sock" ]] && cli "$user" health > /dev/null 2>&1; then return 0; fi
    sleep 1
  done
  cat "$BIN/$user-core.log" >&2
  fail "Core for $user did not start"
}

start_core "$A"
start_core "$B"
pass "independent Cores started for $A and $B on their default Unix sockets"

SA="$(state_of "$A")"
SB="$(state_of "$B")"
for state in "$SA" "$SB"; do
  [[ "$(stat -c %a "$state")" == 700 ]] || fail "$state is not 0700"
  [[ "$(stat -c %a "$state/api-token")" == 600 ]] || fail "$state/api-token is not 0600"
  [[ "$(stat -c %a "$state/state.sqlite3")" == 600 ]] || fail "$state/state.sqlite3 is not 0600"
done
pass "state directories 0700; token and SQLite 0600 for both users"

cli "$A" ask "alpha-private-prompt" | grep -q "alpha-private-prompt" || fail "A chat"
cli "$B" ask "bravo-private-prompt" | grep -q "bravo-private-prompt" || fail "B chat"
cli "$A" history | grep -q alpha-private-prompt || fail "A history missing own prompt"
if cli "$A" history | grep -q bravo-private-prompt; then fail "A sees B's history"; fi
if cli "$B" history | grep -q alpha-private-prompt; then fail "B sees A's history"; fi
pass "each user's history contains only their own conversations"

for target in "$SA" "$SA/api-token" "$SA/state.sqlite3" "$SA/nexus.sock"; do
  if as "$B" cat "$target" > /dev/null 2>&1 || as "$B" ls "$target" > /dev/null 2>&1; then
    fail "$B can access $target"
  fi
done
pass "$B cannot list or read $A's state directory, token, database or socket path"

# B pointing its CLI at A's state directory cannot read the token.
if as "$B" NEXUS_STATE_DIR="$SA" "$DOTNET" "$BIN/cli/NexusOS.Cli.dll" health > /dev/null 2>&1; then
  fail "$B reached $A's Core through NEXUS_STATE_DIR"
fi
# B cannot connect to A's socket at all through the 0700 directory.
connect_probe='
import socket, sys
path, token = sys.argv[1], sys.argv[2]
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
try:
    s.connect(path)
except OSError as error:
    print("connect-denied", error.errno); sys.exit(0)
s.settimeout(5)
s.sendall(("GET /health HTTP/1.1\r\nHost: nexus\r\nAuthorization: Bearer " + token + "\r\n\r\n").encode())
try:
    data = s.recv(4096)
except OSError:
    data = b""
print("http-response" if data else "closed-before-http")
'
result="$(as "$B" python3 -c "$connect_probe" "$SA/nexus.sock" "0000")"
[[ "$result" == connect-denied* ]] || fail "$B connected to $A's socket: $result"
pass "$B's connection to $A's socket is refused by filesystem permissions"

# Defence in depth: loosen permissions and hand B a valid token for A.
token="$(cat "$SA/api-token")"
loosened=("/home/$A" "/home/$A/.local" "/home/$A/.local/state" "$SA")
declare -A original_mode
for dir in "${loosened[@]}"; do original_mode[$dir]="$(stat -c %a "$dir")"; chmod o+x "$dir"; done
chmod 0777 "$SA/nexus.sock"
result="$(as "$B" python3 -c "$connect_probe" "$SA/nexus.sock" "$token")"
[[ "$result" == closed-before-http ]] || fail "SO_PEERCRED did not reject $B: $result"
as "$A" python3 -c "$connect_probe" "$SA/nexus.sock" "$token" | grep -qx http-response || fail "A rejected on own socket"
pass "with loosened permissions and A's token, $B is still rejected by SO_PEERCRED before HTTP"
for dir in "${loosened[@]}"; do chmod "${original_mode[$dir]}" "$dir"; done
chmod 0600 "$SA/nexus.sock"

# Restart preserves the boundaries.
pkill -u "$A" -f NexusOS.Core.dll || fail "could not stop $A's Core"
for _ in $(seq 1 20); do pgrep -u "$A" -f NexusOS.Core.dll > /dev/null || break; sleep 0.5; done
rm -f "$SA/nexus.sock"
start_core "$A"
[[ "$(stat -c %a "$SA")" == 700 ]] || fail "restart changed state directory mode"
cli "$A" history | grep -q alpha-private-prompt || fail "history lost on restart"
pass "restart preserves state, history and permissions"

if grep -rqF "$token" "$BIN"/*.log; then fail "token appeared in logs"; fi
pass "no bearer token in either user's Core log"
echo "Cross-user isolation checks passed."
