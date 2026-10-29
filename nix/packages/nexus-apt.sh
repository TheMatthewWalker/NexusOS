#!/usr/bin/env bash
# apt on NexusOS. NexusOS is built on NixOS, which has no apt of its own, so
# this runs apt inside an Ubuntu container (distrobox, with podman) that
# shares your home folder. Programs installed there run from the terminal,
# and their apps are added to the desktop:
#
#   apt install htop            install a package (no password needed)
#   apt search htop | apt remove htop | apt update | apt upgrade
#   apt shell                   open a terminal inside Ubuntu
#   apt status                  is the Ubuntu container there?
#
# The first use creates the container (a download of about 30 MB). Software
# for the whole system, drivers and services go through NexusOS itself
# (/etc/nixos/local.nix, approved through Guardian), not through apt.
set -euo pipefail

box="${NEXUS_APT_BOX:-ubuntu}"
image="${NEXUS_APT_IMAGE:-docker.io/library/ubuntu:24.04}"

# `sudo apt ...` is natural on Ubuntu; here the container belongs to the
# person, not to root, so run as them.
if [ "$(id -u)" = 0 ] && [ -n "${SUDO_USER:-}" ]; then
  exec runuser -u "$SUDO_USER" -- "$0" "$@"
fi
if [ "$(id -u)" = 0 ]; then
  echo "apt: run this as yourself, not as root." >&2
  exit 1
fi

exists() { podman container exists "$box" 2>/dev/null; }

ensure_box() {
  if exists; then return; fi
  echo "Setting up the Ubuntu container for apt (first time only)…"
  distrobox create --yes --name "$box" --image "$image" --no-entry >/dev/null
  distrobox enter "$box" -- true
  echo "Ready."
}

# New .desktop files in the container become desktop apps.
export_apps() {
  local before="$1" after
  after=$(distrobox enter "$box" -- sh -c 'ls /usr/share/applications 2>/dev/null' || true)
  comm -13 <(echo "$before" | sort) <(echo "$after" | sort) | while read -r entry; do
    [ -n "$entry" ] || continue
    distrobox enter "$box" -- distrobox-export --app "/usr/share/applications/$entry" >/dev/null 2>&1 \
      && echo "Added to your apps: ${entry%.desktop}"
  done
}

cmd="${1:-help}"
case "$cmd" in
  help|-h|--help)
    cat <<'HELP'
apt on NexusOS runs apt inside an Ubuntu container that shares your home folder.

  apt install htop            install a package (no password needed)
  apt search htop | apt remove htop | apt update | apt upgrade
  apt shell                   open a terminal inside Ubuntu
  apt status                  is the Ubuntu container there?

Apps installed this way are added to your desktop. Software for the whole
system goes through NexusOS itself (/etc/nixos/local.nix, approved through Guardian).
HELP
    ;;
  status)
    if exists; then echo "The Ubuntu container '$box' is set up ($image)."; else echo "Not set up yet. It is created by the first apt install."; fi
    ;;
  shell|enter)
    ensure_box
    exec distrobox enter "$box"
    ;;
  install|reinstall)
    ensure_box
    before=$(distrobox enter "$box" -- sh -c 'ls /usr/share/applications 2>/dev/null' || true)
    distrobox enter "$box" -- sudo apt-get update -qq
    distrobox enter "$box" -- sudo apt-get "$@" -y
    export_apps "$before"
    ;;
  remove|purge|autoremove)
    ensure_box
    distrobox enter "$box" -- sudo apt-get "$@" -y
    ;;
  update|upgrade|full-upgrade|dist-upgrade)
    ensure_box
    distrobox enter "$box" -- sudo apt-get update
    [ "$cmd" = update ] || distrobox enter "$box" -- sudo apt-get "$cmd" -y
    ;;
  *)
    ensure_box
    distrobox enter "$box" -- apt "$@"
    ;;
esac
