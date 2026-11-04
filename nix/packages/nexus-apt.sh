#!/usr/bin/env bash
# apt on NexusOS. NexusOS is built on NixOS, which has no apt of its own, so
# this runs apt inside an Ubuntu container (distrobox, with podman). Programs
# installed there run from the terminal, and their apps are added to the
# desktop:
#
#   apt install htop            install a package (no password needed)
#   apt search htop | apt remove htop | apt update | apt upgrade
#   apt shell                   open a terminal inside Ubuntu
#   apt status                  are the Ubuntu containers there?
#   apt --isolated install X    the same, in a container that cannot see your files
#
# Be honest about what this is: the Ubuntu container is NOT a sandbox.
# Ubuntu software runs as you and can read and change your home folder (and
# distrobox shows it the rest of the computer's files under /run/host). Only
# install software you trust there. For software you do not fully trust,
# --isolated uses a second container made with plain podman: its own home
# folder (~/.local/share/nexus-apt/<name>-home), no access to yours or to
# the computer's files, command-line tools only (no desktop apps).
#
# Each install and removal of a named package is reported to Nexus Core
# (`nexus apps record`), so it is in Settings, Timeline with an Undo that
# removes it again, or reinstalls the same version. NEXUS_UNDOES=<entry>
# says this run is that Undo. The first use creates the container (a
# download of about 30 MB). Software for the whole system, drivers and
# services go through NexusOS itself (/etc/nixos/local.nix, approved
# through Guardian), not through apt.
set -euo pipefail

box="${NEXUS_APT_BOX:-ubuntu}"
image="${NEXUS_APT_IMAGE:-docker.io/library/ubuntu:24.04}"
isolated=0
if [ "${1:-}" = "--isolated" ]; then isolated=1; shift; fi
if [ "${NEXUS_APT_ISOLATED:-0}" = 1 ]; then isolated=1; fi
ibox="${box}-isolated"
ihome="${XDG_DATA_HOME:-$HOME/.local/share}/nexus-apt/${ibox}-home"

# `sudo apt ...` is natural on Ubuntu; here the container belongs to the
# person, not to root, so run as them.
if [ "$(id -u)" = 0 ] && [ -n "${SUDO_USER:-}" ]; then
  if [ "$isolated" = 1 ]; then set -- --isolated "$@"; fi
  exec runuser -u "$SUDO_USER" -- "$0" "$@"
fi
if [ "$(id -u)" = 0 ]; then
  echo "apt: run this as yourself, not as root." >&2
  exit 1
fi

current() { if [ "$isolated" = 1 ]; then echo "$ibox"; else echo "$box"; fi; }
exists() { podman container exists "$(current)" 2>/dev/null; }

ensure_box() {
  if [ "$isolated" = 1 ]; then
    if ! exists; then
      echo "Setting up the isolated Ubuntu container (first time only)…"
      mkdir -p "$ihome"
      # No distrobox: nothing of the host is shared but this folder, used as
      # its home. keep-id runs programs as you inside, with no extra rights.
      podman create --name "$ibox" --hostname "$ibox" --userns=keep-id \
        --volume "$ihome:$HOME" --env HOME="$HOME" --workdir "$HOME" \
        --label nexus.apt=isolated "$image" sleep infinity >/dev/null
      echo "Ready. Its home folder is $ihome; it cannot see yours."
    fi
    podman start "$ibox" >/dev/null
    return
  fi
  if exists; then return; fi
  echo "Setting up the Ubuntu container for apt (first time only)…"
  echo "Note: it shares your home folder. It is not a sandbox; use apt --isolated for software you do not trust."
  distrobox create --yes --name "$box" --image "$image" --no-entry >/dev/null
  distrobox enter "$box" -- true
  echo "Ready."
}

# Runs a command in the container: as root for apt (root inside a rootless
# container is still only you), or as you.
as_root() {
  if [ "$isolated" = 1 ]; then podman exec --user root --env DEBIAN_FRONTEND="${DEBIAN_FRONTEND:-readline}" "$ibox" "$@"
  else distrobox enter "$box" -- sudo "$@"; fi
}
as_you() {
  if [ "$isolated" = 1 ]; then
    if [ -t 0 ]; then podman exec -it "$ibox" "$@"; else podman exec "$ibox" "$@"; fi
  else distrobox enter "$box" -- "$@"; fi
}

# New .desktop files in the container become desktop apps (not for the
# isolated container, which has no access to the desktop).
export_apps() {
  [ "$isolated" = 0 ] || return 0
  local before="$1" after
  after=$(as_you sh -c 'ls /usr/share/applications 2>/dev/null' || true)
  comm -13 <(echo "$before" | sort) <(echo "$after" | sort) | while read -r entry; do
    [ -n "$entry" ] || continue
    distrobox enter "$box" -- distrobox-export --app "/usr/share/applications/$entry" >/dev/null 2>&1 \
      && echo "Added to your apps: ${entry%.desktop}"
  done
}

# The package names on the command line (name or name=version), not options.
named() {
  local arg
  for arg in "$@"; do
    case "$arg" in -*) continue ;; esac
    arg="${arg%%=*}"
    if [[ "$arg" =~ ^[a-z0-9][a-z0-9+.-]{0,99}$ ]]; then echo "$arg"; fi
  done
}

# "name version" for each of these packages that is installed now.
versions() {
  [ "$#" -gt 0 ] || return 0
  # shellcheck disable=SC2016
  as_you dpkg-query -W -f='${Package} ${Version} ${db:Status-Status}\n' "$@" 2>/dev/null | awk '$3 == "installed" { print $1, $2 }' || true
}

# Tells Nexus Core, for the timeline and its Undo. Never stops apt.
record() {
  local op="$1" package="$2" version="${3:-}"
  command -v nexus >/dev/null 2>&1 || return 0
  local args=(apps record --source apt --op "$op" --id "$package" --box "$(current)")
  if [ -n "$version" ]; then args+=(--version "$version"); fi
  if [ "$isolated" = 1 ]; then args+=(--isolated); fi
  if [ -n "${NEXUS_UNDOES:-}" ]; then args+=(--undoes "$NEXUS_UNDOES"); NEXUS_UNDOES=""; fi
  nexus "${args[@]}" >/dev/null 2>&1 || echo "(Not added to the timeline: Nexus Core is not running.)" >&2
}

# Records what really changed for the named packages: compares their
# versions before and after.
record_changes() {
  local before="$1" after="$2" package old new
  while read -r package; do
    [ -n "$package" ] || continue
    old=$(awk -v p="$package" '$1 == p { print $2 }' <<<"$before")
    new=$(awk -v p="$package" '$1 == p { print $2 }' <<<"$after")
    if [ -n "$new" ] && [ "$new" != "$old" ]; then record install "$package" "$new"
    elif [ -z "$new" ] && [ -n "$old" ]; then record remove "$package" "$old"; fi
  done
}

cmd="${1:-help}"
case "$cmd" in
  help|-h|--help)
    cat <<'HELP'
apt on NexusOS runs apt inside an Ubuntu container.

  apt install htop            install a package (no password needed)
  apt search htop | apt remove htop | apt update | apt upgrade
  apt shell                   open a terminal inside Ubuntu
  apt status                  are the Ubuntu containers there?

Ubuntu software runs as you and can read your home folder: the container
shares it and is not a sandbox. Install only software you trust.
For software you do not fully trust, add --isolated:

  apt --isolated install X    a separate container with its own home folder
  apt --isolated run X        run a program there
  apt --isolated shell        a terminal inside it

It cannot see your files (only ~/.local/share/nexus-apt/ubuntu-isolated-home)
and is for command-line tools; desktop apps cannot open from it.

Apps installed the normal way are added to your desktop. Installs and
removals are in Settings, Timeline, where they can be undone. Software for
the whole system goes through NexusOS itself (/etc/nixos/local.nix,
approved through Guardian).
HELP
    ;;
  status)
    if podman container exists "$box" 2>/dev/null; then echo "The Ubuntu container '$box' is set up ($image). It shares your home folder."
    else echo "Not set up yet. It is created by the first apt install."; fi
    if podman container exists "$ibox" 2>/dev/null; then echo "The isolated container '$ibox' is set up. Its home folder is $ihome."; fi
    ;;
  shell|enter)
    ensure_box
    if [ "$isolated" = 1 ]; then exec podman exec -it "$ibox" bash; fi
    exec distrobox enter "$box"
    ;;
  run)
    shift
    [ "$#" -gt 0 ] || { echo "apt run: say which program, for example: apt --isolated run htop" >&2; exit 2; }
    ensure_box
    as_you "$@"
    ;;
  install|reinstall)
    ensure_box
    mapfile -t packages < <(named "${@:2}")
    apps_before=$(as_you sh -c 'ls /usr/share/applications 2>/dev/null' || true)
    had=$(versions "${packages[@]}")
    as_root apt-get update -qq
    if ! as_root apt-get "$@" -y; then
      case " $* " in *=*) echo "That exact version may no longer be offered by Ubuntu. apt install <name> gets the current one." >&2 ;; esac
      exit 1
    fi
    record_changes "$had" "$(versions "${packages[@]}")" < <(printf '%s\n' "${packages[@]}")
    export_apps "$apps_before"
    ;;
  remove|purge)
    ensure_box
    mapfile -t packages < <(named "${@:2}")
    had=$(versions "${packages[@]}")
    as_root apt-get "$@" -y
    record_changes "$had" "$(versions "${packages[@]}")" < <(printf '%s\n' "${packages[@]}")
    ;;
  autoremove)
    ensure_box
    as_root apt-get "$@" -y
    ;;
  update|upgrade|full-upgrade|dist-upgrade)
    ensure_box
    as_root apt-get update
    [ "$cmd" = update ] || as_root apt-get "$cmd" -y
    ;;
  *)
    ensure_box
    as_you apt "$@"
    ;;
esac
