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
# Start actual secrets daemon in isolated D-Bus session.
daemon_env=$(printf '%s' 'nexusos-ephemeral-ci-keyring' | timeout 15s gnome-keyring-daemon --unlock --components=secrets)
while IFS= read -r line; do
  case "$line" in
    GNOME_KEYRING_CONTROL=*|SSH_AUTH_SOCK=*) export "$line" ;;
  esac
done <<< "$daemon_env"
timeout 10s gdbus introspect --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets >/dev/null
# Preflight: ensure a real, unlocked collection supports store and lookup.
printf '%s' 'synthetic-preflight-token' | timeout 10s secret-tool store --label='NexusOS CI preflight' app nexusos provider preflight
test "$(timeout 10s secret-tool lookup app nexusos provider preflight)" = 'synthetic-preflight-token'
timeout 10s secret-tool clear app nexusos provider preflight
NEXUS_REAL_SECRET_SERVICE_TEST=1 dotnet test tests/NexusOS.Tests/NexusOS.Tests.csproj --no-restore --filter '(FullyQualifiedName~LinuxRealSecretServiceTests|FullyQualifiedName~CloudAdapterRealKeyringTests)&FullyQualifiedName!~LockedRealKeyringDoesNotSendEvenWithPlaintextEnvironmentKey&FullyQualifiedName!~LockedCollectionCannotSendCloudRequest' --logger 'console;verbosity=normal'
printf '%s' 'synthetic-locked-ci-token' | timeout 10s secret-tool store --label='NexusOS isolated locked test' app nexusos provider openai-compatible
test "$(timeout 10s secret-tool lookup app nexusos provider openai-compatible)" = 'synthetic-locked-ci-token'
collection=$(timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.ReadAlias default | sed -n "s/.*'\(\/org\/freedesktop\/secrets\/collection\/[^']*\)'.*/\1/p")
test -n "$collection"
timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.Lock "['$collection']" >/dev/null
locked=$(timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path "$collection" --method org.freedesktop.DBus.Properties.Get org.freedesktop.Secret.Collection Locked)
grep -q 'true' <<< "$locked"
NEXUS_REAL_SECRET_SERVICE_TEST=1 NEXUS_EXPECT_LOCKED_KEYRING=1 dotnet test tests/NexusOS.Tests/NexusOS.Tests.csproj --no-restore --filter '(FullyQualifiedName~LockedCollectionCannotSendCloudRequest|FullyQualifiedName~LockedRealKeyringDoesNotSendEvenWithPlaintextEnvironmentKey)' --logger 'console;verbosity=normal'
