#!/bin/sh
# Get NexusOS: downloads the newest NexusOS release's installer image,
# checks it and puts it together, ready to write to a USB stick.
#
#   curl -fsSL https://raw.githubusercontent.com/TheMatthewWalker/NexusOS/main/scripts/release/get-nexusos.sh | sh
#   sh get-nexusos.sh [--slim] [v0.1.0] (--slim: the smaller image without the local AI
#                                        model, which downloads once NexusOS is online;
#                                        a version: that release instead of the newest)
#   ... | sh -s -- --slim
#
# GitHub limits each release file to 2 GB, so the image is published in
# parts. This fetches every part (going on where it stopped if interrupted),
# checks each against the release's SHA256SUMS, joins them into one .iso in
# the current folder and checks that too. It needs curl and about twice the
# image's size free while it works (each part is removed once joined).
set -eu

REPO="${NEXUSOS_REPO:-TheMatthewWalker/NexusOS}"
API="${NEXUSOS_API:-https://api.github.com/repos/$REPO/releases}"
EDITION="${NEXUSOS_EDITION:-full}"
if [ "${1:-}" = "--slim" ]; then EDITION=slim; shift; fi
TAG="${1:-latest}"
DIR="${NEXUSOS_DIR:-$(pwd)}"

say() { printf '%s\n' "$*"; }
fail() { printf 'get-nexusos: %s\n' "$*" >&2; exit 1; }

command -v curl > /dev/null || fail "curl is needed (sudo apt install curl, or brew install curl)"
if command -v sha256sum > /dev/null; then hash() { sha256sum "$1" | cut -d' ' -f1; }
elif command -v shasum > /dev/null; then hash() { shasum -a 256 "$1" | cut -d' ' -f1; }
else fail "sha256sum or shasum is needed"; fi

case "$TAG" in latest) url="$API/latest" ;; v*) url="$API/tags/$TAG" ;; *) url="$API/tags/v$TAG" ;; esac
release="$(curl -fsSL -H 'Accept: application/vnd.github+json' "$url")" || fail "could not find the release ($url)"
asset() {  # the download address of the release file named $1
  printf '%s\n' "$release" | tr ',' '\n' | grep '"browser_download_url"' | sed 's/.*"browser_download_url" *: *"\([^"]*\)".*/\1/' \
    | grep "/$1\$" | head -n1
}
version="$(printf '%s\n' "$release" | tr ',' '\n' | grep '"tag_name"' | head -n1 | sed 's/.*"tag_name" *: *"\([^"]*\)".*/\1/')"

mkdir -p "$DIR"
cd "$DIR"
sums_url="$(asset SHA256SUMS)"
[ -n "$sums_url" ] || fail "release $version has no SHA256SUMS"
curl -fsSL -o SHA256SUMS "$sums_url"
# The images (lines without .part-): the full one, and from 0.2 a -slim one.
images="$(grep -v '\.part-' SHA256SUMS | awk '{print $2}')"
if [ "$EDITION" = slim ]; then
  iso="$(printf '%s\n' "$images" | grep -- '-slim-' | head -n1)"
  [ -n "$iso" ] || fail "release $version has no slim image; run without --slim"
else
  iso="$(printf '%s\n' "$images" | grep -v -- '-slim-' | head -n1)"
fi
[ -n "$iso" ] || fail "SHA256SUMS does not name the image"
iso_sum="$(awk -v f="$iso" '$2 == f {print $1}' SHA256SUMS | head -n1)"
parts="$(awk '{print $2}' SHA256SUMS | grep -F -- "$iso.part-" | sort)"
[ -n "$parts" ] || fail "SHA256SUMS lists no parts"

if [ -f "$iso" ] && [ "$(hash "$iso")" = "$iso_sum" ]; then
  say "$iso is already here and checks out."
else
  say "NexusOS $version: downloading $(printf '%s\n' "$parts" | wc -l | tr -d ' ') parts into $DIR"
  for part in $parts; do
    want="$(grep " $part\$" SHA256SUMS | awk '{print $1}')"
    if [ -f "$part" ] && [ "$(hash "$part")" = "$want" ]; then say "  $part: already here"; continue; fi
    part_url="$(asset "$part")"
    [ -n "$part_url" ] || fail "release $version has no $part"
    for attempt in 1 2 3; do
      say "  $part"
      curl -fL --retry 5 --retry-delay 5 -C - -o "$part" "$part_url" || true
      [ "$(hash "$part")" = "$want" ] && break
      say "  $part did not check out; downloading it again"
      rm -f "$part"
      [ "$attempt" -lt 3 ] || fail "$part did not match SHA256SUMS three times; try again later"
    done
  done
  say "Joining the parts into $iso"
  : > "$iso.joining"
  for part in $parts; do cat "$part" >> "$iso.joining" && rm -f "$part"; done
  [ "$(hash "$iso.joining")" = "$iso_sum" ] || { rm -f "$iso.joining"; fail "the joined image did not match SHA256SUMS; run me again"; }
  mv "$iso.joining" "$iso"
fi

say ""
say "Done: $DIR/$iso (checked against the release's SHA256SUMS)."
say "Write it to a USB stick of 16 GB or more, or 8 GB for the slim image (everything on the stick is erased):"
say "  - with balenaEtcher (https://etcher.balena.io): Flash from file, pick the stick, Flash"
say "  - or on Linux: sudo dd if=\"$iso\" of=/dev/sdX bs=4M status=progress conv=fsync   (sdX: the stick, see lsblk)"
say "Then start the computer from the stick. Full steps: https://github.com/$REPO/blob/main/docs/INSTALL.md"
