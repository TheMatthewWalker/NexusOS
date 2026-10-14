#!/usr/bin/env bash
set -euo pipefail
curl --fail --silent http://127.0.0.1:5077/health | grep -q '"status":"ok"'
curl --fail --silent http://127.0.0.1:5077/providers | grep -q '"selected":"local"'
curl --fail --silent http://127.0.0.1:5077/system | grep -q '"os"'
echo 'Read-only Nexus Core smoke checks passed.'
