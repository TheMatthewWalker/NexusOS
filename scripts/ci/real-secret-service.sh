#!/usr/bin/env bash
set -euo pipefail
umask 077
runtime=$(mktemp -d)
data=$(mktemp -d)
home=$(mktemp -d)
cleanup() { rm -rf "$runtime" "$data" "$home"; }
trap cleanup EXIT
export XDG_RUNTIME_DIR="$runtime"
export XDG_DATA_HOME="$data"
export XDG_CONFIG_HOME="$home/.config"
export HOME="$home"
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME/keyrings"
# All isolated XDG paths are set before dbus-run-session, so D-Bus
# activation cannot inherit the runner's original HOME.
if [[ -z "${NEXUS_ISOLATED_DBUS:-}" ]]; then
  export NEXUS_ISOLATED_DBUS=1
  dbus-run-session -- bash "$0"
  exit $?
fi
# --login creates the keyring on a clean profile; --unlock alone cannot.
# Keep the daemon password confined to this ephemeral CI session.
printf '%s' 'nexusos-ephemeral-ci-keyring' | timeout 15s gnome-keyring-daemon --login --components=secrets >/dev/null
timeout 15s gnome-keyring-daemon --start --components=secrets >/dev/null
timeout 10s gdbus introspect --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets >/dev/null
# Preflight: ensure a real, unlocked collection supports store and lookup.
printf '%s' 'synthetic-preflight-token' | timeout 10s secret-tool store --label='NexusOS CI preflight' app nexusos provider preflight
test "$(timeout 10s secret-tool lookup app nexusos provider preflight)" = 'synthetic-preflight-token'
timeout 10s secret-tool clear app nexusos provider preflight
# Each phase must execute an exact number of tests, so a broken environment
# gate cannot turn the real-keyring tests into vacuous passes.
run_phase() {
  local expected="$1"; shift
  local log
  log=$(mktemp)
  "$@" | tee "$log"
  grep -Eq "Passed:[[:space:]]+$expected([^0-9]|$)" "$log" || { echo "Expected exactly $expected real-keyring tests to run" >&2; exit 1; }
}
run_phase 4 env NEXUS_REAL_SECRET_SERVICE_TEST=1 dotnet test tests/NexusOS.Tests/NexusOS.Tests.csproj --no-restore --filter '(FullyQualifiedName~LinuxRealSecretServiceTests|FullyQualifiedName~CloudAdapterRealKeyringTests)&FullyQualifiedName!~LockedRealKeyringDoesNotSendEvenWithPlaintextEnvironmentKey&FullyQualifiedName!~LockedCollectionCannotSendCloudRequest' --logger 'console;verbosity=normal'
printf '%s' 'synthetic-locked-ci-token' | timeout 10s secret-tool store --label='NexusOS isolated locked test' app nexusos provider openai-compatible
test "$(timeout 10s secret-tool lookup app nexusos provider openai-compatible)" = 'synthetic-locked-ci-token'
collection=$(timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.ReadAlias default | sed -n "s/.*'\(\/org\/freedesktop\/secrets\/collection\/[^']*\)'.*/\1/p")
test -n "$collection"
timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.Lock "['$collection']" >/dev/null
locked=$(timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path "$collection" --method org.freedesktop.DBus.Properties.Get org.freedesktop.Secret.Collection Locked)
grep -q 'true' <<< "$locked"
run_phase 2 env NEXUS_REAL_SECRET_SERVICE_TEST=1 NEXUS_EXPECT_LOCKED_KEYRING=1 dotnet test tests/NexusOS.Tests/NexusOS.Tests.csproj --no-restore --filter '(FullyQualifiedName~LockedCollectionCannotSendCloudRequest|FullyQualifiedName~LockedRealKeyringDoesNotSendEvenWithPlaintextEnvironmentKey)' --logger 'console;verbosity=normal'
