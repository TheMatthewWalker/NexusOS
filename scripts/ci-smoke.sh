#!/usr/bin/env bash
set -euo pipefail
export NEXUS_STATE_DIR="$(mktemp -d)"
export NEXUS_API_MODE=tcp
# Search may only index folders inside "home"; use a throwaway one, outside the state directory.
export NEXUS_SEARCH_HOME="$(mktemp -d)"
trap 'status=$?; if [[ $status -ne 0 ]]; then echo "Smoke failure at line $LINENO (exit $status)"; cat out/core.log out/mock.log 2>/dev/null || true; fi' ERR

cleanup() {
  kill "${CORE_PID:-}" "${MOCK_PID:-}" 2>/dev/null || true
  rm -rf "$NEXUS_STATE_DIR" "$NEXUS_SEARCH_HOME"
}
trap cleanup EXIT

python3 scripts/mock-ollama.py > out/mock.log 2>&1 &
MOCK_PID=$!
NEXUS_OLLAMA_URL=http://127.0.0.1:11435 NEXUS_LLAMA_ENDPOINT=http://127.0.0.1:11435 dotnet out/core/NexusOS.Core.dll > out/core.log 2>&1 &
CORE_PID=$!

ready=0
for i in $(seq 1 60); do
  if curl --fail --silent -H "Authorization: Bearer $(cat "$NEXUS_STATE_DIR/api-token")" http://127.0.0.1:5077/health > /dev/null; then ready=1; break; fi
  if ! kill -0 "$CORE_PID" 2>/dev/null; then echo "Core exited unexpectedly"; cat out/core.log; exit 1; fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then echo "Core did not become healthy"; cat out/core.log; exit 1; fi

python3 - <<'PY'
import socket
import time
for _ in range(50):
    try:
        with socket.create_connection(("127.0.0.1", 11435), 0.2):
            break
    except OSError:
        time.sleep(0.1)
else:
    raise SystemExit("Mock Ollama did not become ready")
PY
test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5077/health)" = 401
test "$(curl -s -o /dev/null -w '%{http_code}' -H 'Authorization: Bearer invalid' http://127.0.0.1:5077/history)" = 401
bash scripts/smoke.sh
dotnet out/cli/NexusOS.Cli.dll health | tee /dev/stderr | grep -q '"status": "ok"'
dotnet out/cli/NexusOS.Cli.dll providers | tee /dev/stderr | grep -q '"selected": "local"'
dotnet out/cli/NexusOS.Cli.dll use local | tee /dev/stderr | grep -q '"selected": "local"'
dotnet out/cli/NexusOS.Cli.dll ask "Hello Nexus" | tee /dev/stderr | grep -q 'Nexus mock response: Hello Nexus'
dotnet out/cli/NexusOS.Cli.dll history | tee /dev/stderr | grep -q 'Hello Nexus'
dotnet out/cli/NexusOS.Cli.dll clear-history
test "$(dotnet out/cli/NexusOS.Cli.dll history | tr -d '[:space:]')" = "[]"

# Invalid selections and malformed chat must be rejected.
test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $(cat "$NEXUS_STATE_DIR/api-token")" -H 'Content-Type: application/json' -d '{"name":"not-a-provider"}' http://127.0.0.1:5077/providers/select)" = 400
test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $(cat "$NEXUS_STATE_DIR/api-token")" -H 'Content-Type: application/json' -d '{"prompt":""}' http://127.0.0.1:5077/chat)" = 400
# Kestrel's 64 KiB request body limit (not exercised by in-process API tests).
python3 -c 'import json; print(json.dumps({"prompt": "a" * 70000}))' > "$NEXUS_STATE_DIR/oversized.json"
test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $(cat "$NEXUS_STATE_DIR/api-token")" -H 'Content-Type: application/json' --data-binary @"$NEXUS_STATE_DIR/oversized.json" http://127.0.0.1:5077/chat)" = 413
echo "Nexus Core end-to-end smoke tests passed."

# Guardian must generate a safe preview and reject arbitrary configuration.
TOKEN="$(cat "$NEXUS_STATE_DIR/api-token")"
curl -fsS -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"description":"Enable printing","changes":[{"option":"services.printing.enable","enabled":true}]}' \
  http://127.0.0.1:5077/guardian/preview | grep -q 'services.printing.enable'
test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"description":"unsafe","changes":[{"option":"imports","enabled":true}]}' \
  http://127.0.0.1:5077/guardian/preview)" = 400

TOKEN="$(cat "$NEXUS_STATE_DIR/api-token")"
curl -fsS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5077/inference/status | grep -q '"available":true'
curl -fsS -X POST -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5077/inference/unload | grep -q '"unloadRequested":true'

# Core never approves Guardian changes itself; without the root broker it refuses.
TOKEN="$(cat "$NEXUS_STATE_DIR/api-token")"
test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"description":"Enable printing","changes":[{"option":"services.printing.enable","enabled":true}]}' \
  http://127.0.0.1:5077/guardian/submit)" = 503
test "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" -X POST http://127.0.0.1:5077/guardian/acknowledge)" = 404

# Private search: choose a folder, index it locally (mock /api/embed), find it by
# words and by meaning, refuse a folder outside home, then erase the index.
mkdir -p "$NEXUS_SEARCH_HOME/Documents"
echo "Plumber invoice overdue" > "$NEXUS_SEARCH_HOME/Documents/money.txt"
dotnet out/cli/NexusOS.Cli.dll search sources add "$NEXUS_SEARCH_HOME/Documents" | grep '"indexing": true' > /dev/null
for i in $(seq 1 30); do
  if dotnet out/cli/NexusOS.Cli.dll search status | grep '"embeddedChunks": 1' > /dev/null; then break; fi
  sleep 1
done
dotnet out/cli/NexusOS.Cli.dll search status | tee /dev/stderr | grep '"available": true' > /dev/null
dotnet out/cli/NexusOS.Cli.dll search plumber | tee /dev/stderr | grep 'money.txt' > /dev/null
dotnet out/cli/NexusOS.Cli.dll search bill | tee /dev/stderr | grep 'Close in meaning' > /dev/null
if dotnet out/cli/NexusOS.Cli.dll search sources add /etc; then echo "Search accepted a folder outside home"; exit 1; fi
dotnet out/cli/NexusOS.Cli.dll search erase | grep '"verified": true' > /dev/null
dotnet out/cli/NexusOS.Cli.dll search plumber | grep 'No matches.' > /dev/null

TOKEN="$(cat "$NEXUS_STATE_DIR/api-token")"
curl -fsS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5077/inference/telemetry | grep -q '"ramTotalBytes"'

dotnet out/cli/NexusOS.Cli.dll use llama-cpp | grep -q 'llama-cpp'
dotnet out/cli/NexusOS.Cli.dll ask "Hello llama" | grep -q 'Nexus llama mock: Hello llama'
