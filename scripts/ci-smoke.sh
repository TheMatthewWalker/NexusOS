#!/usr/bin/env bash
set -euo pipefail

cleanup() {
  kill "${CORE_PID:-}" "${MOCK_PID:-}" 2>/dev/null || true
}
trap cleanup EXIT

python3 scripts/mock-ollama.py > out/mock.log 2>&1 &
MOCK_PID=$!
NEXUS_OLLAMA_URL=http://127.0.0.1:11435 dotnet out/core/NexusOS.Core.dll > out/core.log 2>&1 &
CORE_PID=$!

ready=0
for i in $(seq 1 60); do
  if curl --fail --silent http://127.0.0.1:5077/health > /dev/null; then ready=1; break; fi
  if ! kill -0 "$CORE_PID" 2>/dev/null; then echo "Core exited unexpectedly"; cat out/core.log; exit 1; fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then echo "Core did not become healthy"; cat out/core.log; exit 1; fi

bash scripts/smoke.sh
dotnet out/cli/NexusOS.Cli.dll health | grep -q '"status": "ok"'
dotnet out/cli/NexusOS.Cli.dll providers | grep -q '"selected": "local"'
dotnet out/cli/NexusOS.Cli.dll use local | grep -q '"selected": "local"'
dotnet out/cli/NexusOS.Cli.dll ask "Hello Nexus" | grep -q 'Nexus mock response: Hello Nexus'

# Invalid selections and malformed chat must be rejected.
test "$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -d '{"name":"not-a-provider"}' http://127.0.0.1:5077/providers/select)" = 400
test "$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -d '{"prompt":""}' http://127.0.0.1:5077/chat)" = 400
echo "Nexus Core end-to-end smoke tests passed."
