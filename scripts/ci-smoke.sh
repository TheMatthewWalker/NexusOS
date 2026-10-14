#!/usr/bin/env bash
set -euo pipefail
export NEXUS_STATE_DIR="$(mktemp -d)"
trap 'status=$?; if [[ $status -ne 0 ]]; then echo "Smoke failure at line $LINENO (exit $status)"; cat out/core.log out/mock.log 2>/dev/null || true; fi' ERR

cleanup() {
  kill "${CORE_PID:-}" "${MOCK_PID:-}" 2>/dev/null || true
  rm -rf "$NEXUS_STATE_DIR"
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

# Record a reviewed Guardian acknowledgement; no configuration is applied.
python3 - <<'PY'
import json, os, urllib.request
base = "http://127.0.0.1:5077"
token = open(os.path.join(os.environ["NEXUS_STATE_DIR"],"api-token"),encoding="utf-8").read().strip()
headers = {"Authorization":"Bearer "+token,"Content-Type":"application/json"}
plan = {"description":"Enable printing","changes":[{"option":"services.printing.enable","enabled":True}]}
def send(path, payload):
    request = urllib.request.Request(base+path,data=json.dumps(payload).encode(),headers=headers,method="POST")
    with urllib.request.urlopen(request) as response:
        return json.load(response)
preview = send("/guardian/preview",plan)
ack = {**plan,"expectedPlanId":preview["id"],"acknowledgement":"I APPROVE"}
result = send("/guardian/acknowledge",ack)
assert result["accepted"] is True and result["executed"] is False
PY

TOKEN="$(cat "$NEXUS_STATE_DIR/api-token")"
curl -fsS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5077/inference/telemetry | grep -q '"ramTotalBytes"'

dotnet out/cli/NexusOS.Cli.dll use llama-cpp | grep -q 'llama-cpp'
dotnet out/cli/NexusOS.Cli.dll ask "Hello llama" | grep -q 'Nexus llama mock: Hello llama'
