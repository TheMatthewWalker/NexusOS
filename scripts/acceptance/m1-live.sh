#!/usr/bin/env bash
# M1 live acceptance: exercise Nexus Core and the CLI against REAL inference
# engines (Ollama and, optionally, llama.cpp). Run from the repository root
# after publishing Core to out/core and the CLI to out/cli.
#
# Required: a running Ollama with $NEXUS_OLLAMA_MODEL pulled.
# Optional: NEXUS_LLAMA_SERVER_BIN + NEXUS_LLAMA_MODEL_PATH to exercise a
#           Core-managed llama.cpp server, including unload.
# Optional: NEXUS_M1_EVIDENCE=<file> appends a Markdown evidence record.
#
# Cloud: without an operator key this script checks only fail-closed paths
# (egress disabled, credential missing). A live cloud answer is a separate,
# manual step documented in docs/evidence/M1.md.
set -euo pipefail

: "${NEXUS_OLLAMA_MODEL:?Set NEXUS_OLLAMA_MODEL to a model already pulled into Ollama}"
OLLAMA_URL="${NEXUS_OLLAMA_URL:-http://127.0.0.1:11434}"
STATE="$(mktemp -d)"
LOGS="$(mktemp -d)"
CORE_PID=""
export NEXUS_STATE_DIR="$STATE"
unset NEXUS_API_MODE  # The product default (Unix socket on Linux) is what M1 accepts.
DOTNET="$(command -v dotnet)"
CLI=("$DOTNET" out/cli/NexusOS.Cli.dll)
PASS=0
RESULTS=()

cleanup() {
  stop_core
  rm -rf "$STATE"
}
trap cleanup EXIT

fail() { echo "M1 FAIL: $*" >&2; [[ -f "$LOGS/core.log" ]] && tail -20 "$LOGS/core.log" >&2; exit 1; }
pass() { PASS=$((PASS + 1)); RESULTS+=("- [x] $1"); echo "M1 PASS: $1"; }

start_core() {
  rm -f "$STATE/nexus.sock"
  env "$@" "$DOTNET" out/core/NexusOS.Core.dll > "$LOGS/core.log" 2>&1 &
  CORE_PID=$!
  for _ in $(seq 1 60); do
    if [[ -S "$STATE/nexus.sock" ]] && "${CLI[@]}" health > /dev/null 2>&1; then return 0; fi
    kill -0 "$CORE_PID" 2>/dev/null || fail "Core exited during startup"
    sleep 1
  done
  fail "Core did not become healthy"
}

stop_core() {
  if [[ -n "$CORE_PID" ]]; then
    kill "$CORE_PID" 2>/dev/null || true
    wait "$CORE_PID" 2>/dev/null || true
    CORE_PID=""
  fi
}

# Expect a CLI command to fail with a message matching a pattern.
expect_error() {
  local pattern="$1"; shift
  local output
  if output="$("${CLI[@]}" "$@" 2>&1)"; then fail "expected failure for: $*"; fi
  grep -qiE "$pattern" <<< "$output" || fail "unexpected error for '$*': $output"
  echo "$output"
}

curl -fsS "$OLLAMA_URL/api/tags" | grep -q "\"${NEXUS_OLLAMA_MODEL%%:*}" \
  || fail "Ollama at $OLLAMA_URL does not list model $NEXUS_OLLAMA_MODEL"

start_core NEXUS_OLLAMA_MODEL="$NEXUS_OLLAMA_MODEL" NEXUS_LLAMA_SERVER_BIN="${NEXUS_LLAMA_SERVER_BIN:-}" \
  NEXUS_LLAMA_MODEL_PATH="${NEXUS_LLAMA_MODEL_PATH:-}" NEXUS_LLAMA_PORT="${NEXUS_LLAMA_PORT:-18081}" \
  NEXUS_MAX_OUTPUT_TOKENS=48
pass "Core starts on the default Unix socket"

# 5077 is 0x13D5; state 0A is LISTEN. Reads the kernel table directly so no tool is required.
tcp_tables=()
for table in /proc/net/tcp /proc/net/tcp6; do [[ -r "$table" ]] && tcp_tables+=("$table"); done
[[ ${#tcp_tables[@]} -gt 0 ]] || fail "no readable /proc/net TCP table"
if awk '$4 == "0A" && $2 ~ /:13D5$/ { found = 1 } END { exit !found }' "${tcp_tables[@]}"; then
  fail "TCP listener present in default mode"
fi
pass "No TCP listener in default mode (loopback/network exposure impossible)"
test "$(stat -c %a "$STATE")" = 700 || fail "state directory is not mode 0700"
test "$(stat -c %a "$STATE/api-token")" = 600 || fail "api-token is not mode 0600"
pass "State directory 0700 and API token 0600"

"${CLI[@]}" health | grep -q '"status": "ok"' || fail "health"
"${CLI[@]}" system | grep -q '"os"' || fail "system"
pass "CLI health and read-only system inventory"

"${CLI[@]}" providers | grep -q '"selected": "local"' || fail "default provider"
"${CLI[@]}" use local | grep -q '"selected": "local"' || fail "use local"
expect_error "Unknown provider" use not-a-provider > /dev/null
"${CLI[@]}" use openai-compatible | grep -q '"selected": "openai-compatible"' || fail "use cloud"
"${CLI[@]}" use local > /dev/null
pass "Provider listing and selection are deterministic; unknown providers rejected"

"${CLI[@]}" status | grep -q '"available": true' || fail "Ollama status"
answer_json="$("${CLI[@]}" ask "Reply with one short sentence about Linux.")" || fail "real Ollama ask"
answer="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["answer"])' <<< "$answer_json")"
[[ -n "${answer//[[:space:]]/}" ]] || fail "empty Ollama answer"
grep -q '"provider": "local"' <<< "$answer_json" || fail "answer not attributed to local provider"
pass "Real Ollama model '$NEXUS_OLLAMA_MODEL' returned a non-empty answer (${#answer} chars)"
"${CLI[@]}" history | grep -q 'about Linux' || fail "history missing prompt"
pass "Conversation recorded in SQLite history"

expect_error "Prompt must be between" ask " " > /dev/null
pass "Blank prompt rejected"

"${CLI[@]}" use openai-compatible > /dev/null
expect_error "transmission is disabled" ask "hello" > /dev/null
pass "Cloud provider refuses to send prompts without explicit egress consent"

if [[ -n "${NEXUS_LLAMA_SERVER_BIN:-}" ]]; then
  "${CLI[@]}" use llama-cpp > /dev/null
  llama_json="$("${CLI[@]}" ask "Say hello.")" || fail "real llama.cpp ask"
  grep -q '"provider": "llama-cpp"' <<< "$llama_json" || fail "llama answer attribution"
  pgrep -f -- "^$NEXUS_LLAMA_SERVER_BIN --model" > /dev/null || fail "managed llama-server not running"
  "${CLI[@]}" unload | grep -q '"unloadRequested": true' || fail "llama unload"
  sleep 1
  if pgrep -f -- "^$NEXUS_LLAMA_SERVER_BIN --model" > /dev/null; then fail "llama-server still running after unload"; fi
  pass "Real llama.cpp server started on demand, answered, and stopped on unload"
fi

"${CLI[@]}" use local > /dev/null
"${CLI[@]}" unload | grep -q '"unloadRequested": true' || fail "Ollama unload"
pass "Ollama unload request accepted"
stop_core

start_core NEXUS_OLLAMA_MODEL="nexus-m1-missing-model"
expect_error "ollama pull nexus-m1-missing-model" ask "hello" > /dev/null
pass "Missing local model gives an actionable 'ollama pull' error"
stop_core

# PATH without secret-tool guarantees no real keyring credential can be used here.
start_core NEXUS_CLOUD_EGRESS_ALLOWED=true PATH="/nonexistent"
"${CLI[@]}" use openai-compatible > /dev/null
expect_error "secret-tool|keyring" ask "hello" > /dev/null
pass "Consented cloud request without an OS keyring credential fails closed"
stop_core

start_core NEXUS_OLLAMA_URL="http://127.0.0.1:9"
"${CLI[@]}" use local > /dev/null
expect_error "unreachable" ask "hello" > /dev/null
pass "Stopped local engine reported as unreachable"
stop_core

if (env NEXUS_OLLAMA_URL="http://192.168.1.10:11434" timeout 20 "$DOTNET" out/core/NexusOS.Core.dll > "$LOGS/bad.log" 2>&1); then
  fail "Core started with a non-loopback local model endpoint"
fi
grep -q "must be HTTP on loopback" "$LOGS/bad.log" || fail "startup validation message"
pass "Non-loopback local endpoint rejected at startup"

token="$(cat "$STATE/api-token")"
if grep -rqF "$token" "$LOGS"; then fail "API token appeared in logs"; fi
if grep -rqF "about Linux" "$LOGS"; then fail "prompt text appeared in logs"; fi
pass "API token and prompt text absent from Core logs"

echo "M1 live acceptance: $PASS checks passed."
if [[ -n "${NEXUS_M1_EVIDENCE:-}" ]]; then
  {
    echo "### Run $(date -u +%Y-%m-%dT%H:%MZ) — model \`$NEXUS_OLLAMA_MODEL\`"
    echo
    echo "Ollama $(curl -fsS "$OLLAMA_URL/api/version" | python3 -c 'import json,sys; print(json.load(sys.stdin)["version"])')," \
         "llama.cpp: ${NEXUS_LLAMA_SERVER_BIN:-not exercised}, host: $(uname -srm)"
    echo
    printf '%s\n' "${RESULTS[@]}"
    echo
  } >> "$NEXUS_M1_EVIDENCE"
fi
