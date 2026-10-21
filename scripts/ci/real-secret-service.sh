#!/usr/bin/env bash
set -euo pipefail
umask 077
runtime=$(mktemp -d)
data=$(mktemp -d)
cleanup() { rm -rf "$runtime" "$data"; }
trap cleanup EXIT
export XDG_RUNTIME_DIR="$runtime"
export XDG_DATA_HOME="$data"
printf '%s\n' 'nexusos-ephemeral-ci-keyring' | timeout 15s gnome-keyring-daemon --unlock --components=secrets >/dev/null
timeout 10s gdbus introspect --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets >/dev/null
NEXUS_REAL_SECRET_SERVICE_TEST=1 dotnet test tests/NexusOS.Tests/NexusOS.Tests.csproj --no-restore --filter 'FullyQualifiedName~LinuxRealSecretServiceTests&FullyQualifiedName!~LockedCollectionCannotSendCloudRequest' --logger 'console;verbosity=normal'
printf '%s' 'synthetic-locked-ci-token' | timeout 10s secret-tool store --label='NexusOS isolated locked test' app nexusos provider openai-compatible
test "$(timeout 10s secret-tool lookup app nexusos provider openai-compatible)" = 'synthetic-locked-ci-token'
collection=$(timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.ReadAlias default | sed -n "s/.*'\(\/org\/freedesktop\/secrets\/collection\/[^']*\)'.*/\1/p")
test -n "$collection"
timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path /org/freedesktop/secrets --method org.freedesktop.Secret.Service.Lock "['$collection']" >/dev/null
locked=$(timeout 10s gdbus call --session --dest org.freedesktop.secrets --object-path "$collection" --method org.freedesktop.DBus.Properties.Get org.freedesktop.Secret.Collection Locked)
grep -q 'true' <<< "$locked"
NEXUS_REAL_SECRET_SERVICE_TEST=1 NEXUS_EXPECT_LOCKED_KEYRING=1 dotnet test tests/NexusOS.Tests/NexusOS.Tests.csproj --no-restore --filter 'FullyQualifiedName~LockedCollectionCannotSendCloudRequest' --logger 'console;verbosity=normal'
