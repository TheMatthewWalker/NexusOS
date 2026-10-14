#!/usr/bin/env bash
set -euo pipefail
TOKEN="$(cat "$NEXUS_STATE_DIR/api-token")"
curl --fail --silent -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5077/health | grep -q '"status":"ok"'
curl --fail --silent -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5077/providers | grep -q '"selected":"local"'
curl --fail --silent -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5077/system | grep -q '"os"'
echo 'Authenticated Nexus Core smoke checks passed.'
