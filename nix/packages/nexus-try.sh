#!/usr/bin/env bash
# nexus-try: run an app from the system's pinned nixpkgs in a throwaway
# bubblewrap sandbox. Nothing is installed into any profile, and the sandbox's
# home folder is a tmpfs that disappears when the app exits. To keep the app,
# `nexus try keep <attr>` makes a normal Guardian install proposal.
#
# This is a convenience sandbox, not a hard security boundary: see
# docs/ARCHITECTURE.md ("Try before you install") for what it does not stop.
set -euo pipefail

prog=nexus-try
# Test hook only: lets the unit tests use a fake store directory.
store=${NEXUS_TRY_STORE:-/nix/store}

usage() {
  cat <<EOF
Usage: $prog [--no-network] [--file PATH] [--shell] <nixpkgs-attr> [-- ARGS...]

Runs <nixpkgs-attr> (for example: hello, gnome-calculator, python3Packages.ipython)
in a throwaway sandbox. Nothing is installed and nothing is kept.

  --no-network   run without network access (default: network on)
  --file PATH    hand one file in, read-only, at ~/Trial/<name>
  --shell        open a shell inside the sandbox with the app on PATH,
                 instead of starting the app; ARGS go to the shell
  -h, --help     show this help

To install the app for real afterwards: nexus try keep <nixpkgs-attr>
EOF
}

die() { echo "$prog: $*" >&2; exit 2; }

# Same rule as the Guardian package schema (schema/guardian-options.json):
# plain attribute paths only, never flake references, paths or Nix syntax.
valid_attr() {
  local LC_ALL=C attr=$1 segment
  local re='^[A-Za-z_][A-Za-z0-9_-]{0,63}(\.[A-Za-z_][A-Za-z0-9_-]{0,63}){0,3}$'
  [[ $attr =~ $re ]] || return 1
  local -a segments
  IFS=. read -r -a segments <<<"$attr"
  for segment in "${segments[@]}"; do
    case $segment in
      assert | else | if | in | inherit | let | or | rec | then | with) return 1 ;;
    esac
  done
}

network=1 shell=0 file="" attr="" have_file=0
app_args=()
while (($#)); do
  case $1 in
    --no-network) network=0 ;;
    --shell) shell=1 ;;
    --file)
      (($# >= 2)) || die "--file needs a path"
      ((have_file == 0)) || die "only one --file can be handed in"
      file=$2 have_file=1
      shift
      ;;
    -h | --help) usage; exit 0 ;;
    --)
      shift
      app_args=("$@")
      break
      ;;
    -*) die "unknown option '$1' (see --help)" ;;
    *)
      [[ -z $attr ]] || die "only one app can be tried at a time; put app arguments after --"
      attr=$1
      ;;
  esac
  shift
done

[[ -n $attr ]] || { usage >&2; exit 2; }
valid_attr "$attr" || die "'$attr' is not a nixpkgs package name (use a plain attribute such as 'hello' or 'python3Packages.ipython')"

trial_file="" trial_name=""
if ((have_file)); then
  [[ -f $file && -r $file ]] || die "--file '$file' is not a readable file"
  trial_file=$(realpath -e -- "$file")
  trial_name=${trial_file##*/}
  [[ $trial_name != *$'\n'* ]] || die "--file names cannot contain newlines"
fi

home=${HOME:-}
[[ $home == /* && $home != *$'\n'* ]] || die "HOME must be an absolute path"
case $home/ in
  # The sandbox home must not land inside anything bound in from the host.
  // | /nix/* | /etc/* | /dev/* | /proc/* | /sys/* | /run/* | /usr/* | /bin/*) die "unsupported HOME '$home'" ;;
esac

nix=(nix --extra-experimental-features "nix-command flakes")
echo "$prog: fetching $attr into the Nix store (nothing is installed)..." >&2
# The system's pinned registry resolves "nixpkgs"; --no-link leaves no GC root
# or profile entry behind.
if ! outputs=$("${nix[@]}" build --no-link --print-out-paths "nixpkgs#$attr"); then
  die "could not fetch or build '$attr' from nixpkgs"
fi
mapfile -t outs <<<"$outputs"
for out in "${outs[@]}"; do
  [[ $out =~ ^${store}/[0-9a-z]{32}-[A-Za-z0-9+._?=-]+$ ]] || die "unexpected build output '$out'"
done

path_dirs=() data_dirs=()
for out in "${outs[@]}"; do
  [[ -d $out/bin ]] && path_dirs+=("$out/bin")
  [[ -d $out/share ]] && data_dirs+=("$out/share")
done

shell_path=""
if ((shell)); then
  cmd=("${NEXUS_TRY_SHELL:-bash}" "${app_args[@]}")
  # Basic tools for poking around (coreutils, grep), after the app's own.
  shell_path=${NEXUS_TRY_SHELL_PATH:-}
else
  main=$("${nix[@]}" eval --raw "nixpkgs#$attr" \
    --apply 'p: p.meta.mainProgram or (p.pname or (builtins.parseDrvName p.name).name)' 2>/dev/null || true)
  exe=""
  if [[ $main =~ ^[A-Za-z0-9._+-]+$ ]]; then
    for dir in "${path_dirs[@]}"; do
      [[ -x $dir/$main && ! -d $dir/$main ]] && { exe=$dir/$main; break; }
    done
  fi
  if [[ -z $exe ]]; then
    candidates=()
    for dir in "${path_dirs[@]}"; do
      for f in "$dir"/*; do [[ -x $f && ! -d $f ]] && candidates+=("$f"); done
    done
    ((${#candidates[@]} == 1)) || die "'$attr' has no single program to start; try --shell"
    exe=${candidates[0]}
  fi
  cmd=("$exe" "${app_args[@]}")
fi

join() { local IFS=:; echo "$*"; }
uid=$(id -u)
user=$(id -un 2>/dev/null || echo "user$uid")

args=(
  --unshare-all # user, IPC, PID, network, UTS and cgroup namespaces
  --die-with-parent
  --new-session # no controlling terminal: blocks TIOCSTI input injection
  --cap-drop ALL
  --clearenv
  --ro-bind "$store" "$store"
  --proc /proc
  --dev /dev
  --tmpfs /tmp
  --dir /var/tmp
  # A fresh, empty home on the sandbox's tmpfs root; the real home is never mounted.
  --perms 0700 --dir "$home"
  --chdir "$home"
  --hostname nexus-try
)
((network)) && args+=(--share-net)

# System files apps need (read-only); on NixOS most point into /etc/static.
for f in /etc/static /etc/passwd /etc/group /etc/hosts /etc/resolv.conf /etc/nsswitch.conf \
  /etc/localtime /etc/zoneinfo /etc/ssl /etc/pki /etc/fonts /etc/machine-id /etc/os-release \
  /etc/xdg /etc/pipewire /etc/alsa /etc/asound.conf /etc/vulkan /etc/egl /bin/sh /usr/bin/env; do
  args+=(--ro-bind-try "$f" "$f")
done
for f in /run/current-system /run/opengl-driver /run/opengl-driver-32; do
  [[ -e $f ]] && args+=(--symlink "$(readlink -f "$f")" "$f")
done
# GPU and the read-only parts of /sys that Mesa and toolkits probe.
args+=(--dev-bind-try /dev/dri /dev/dri)
for f in /sys/dev /sys/devices /sys/bus /sys/class; do args+=(--ro-bind-try "$f" "$f"); done

env_args=(
  --setenv HOME "$home" --setenv USER "$user" --setenv LOGNAME "$user"
  --setenv PATH "$(join "${path_dirs[@]}" ${shell_path:+"$shell_path"} /run/current-system/sw/bin)"
  --setenv XDG_DATA_DIRS "$(join "${data_dirs[@]}" /run/current-system/sw/share)"
)
for name in LANG LC_ALL TERM COLORTERM LOCALE_ARCHIVE TZDIR XCURSOR_THEME XCURSOR_SIZE XDG_CURRENT_DESKTOP; do
  [[ -n ${!name:-} ]] && env_args+=(--setenv "$name" "${!name}")
done

# Wayland and PipeWire (and its PulseAudio socket) so GUI apps and sound work.
# No X11 socket and no D-Bus session bus are passed in.
runtime=${XDG_RUNTIME_DIR:-}
passed=()
if [[ $runtime == /* && -d $runtime ]]; then
  args+=(--perms 0700 --dir "$runtime")
  env_args+=(--setenv XDG_RUNTIME_DIR "$runtime")
  if [[ -n ${WAYLAND_DISPLAY:-} && $WAYLAND_DISPLAY != */* ]]; then
    wayland=$runtime/$WAYLAND_DISPLAY
    if [[ -S $wayland ]]; then
      args+=(--ro-bind "$wayland" "$runtime/$WAYLAND_DISPLAY")
      env_args+=(--setenv WAYLAND_DISPLAY "$WAYLAND_DISPLAY" --setenv XDG_SESSION_TYPE wayland)
      passed+=(Wayland)
    fi
  fi
  if [[ -S $runtime/pipewire-0 ]]; then
    args+=(--ro-bind "$runtime/pipewire-0" "$runtime/pipewire-0")
    passed+=(PipeWire)
  fi
  if [[ -S $runtime/pulse/native ]]; then
    args+=(--dir "$runtime/pulse" --ro-bind "$runtime/pulse/native" "$runtime/pulse/native")
    env_args+=(--setenv PULSE_SERVER "unix:$runtime/pulse/native")
  fi
fi

if [[ -n $trial_file ]]; then
  args+=(--dir "$home/Trial" --ro-bind "$trial_file" "$home/Trial/$trial_name")
fi

if ((network)); then summary="network on"; else summary="network off"; fi
for p in "${passed[@]}"; do summary+=", $p"; done
[[ -n $trial_file ]] && summary+=", ~/Trial/$trial_name read-only"
echo "$prog: starting $attr in a throwaway sandbox ($summary)" >&2

# Ctrl-C ends the trial (bwrap takes the app with it) but we still report.
trap ':' INT TERM HUP
set +e
"${NEXUS_TRY_BWRAP:-bwrap}" "${args[@]}" "${env_args[@]}" -- "${cmd[@]}"
status=$?
set -e
trap - INT TERM HUP

echo >&2
echo "$prog: the trial of $attr has ended. Nothing was kept: its home folder is gone and nothing was installed into any profile." >&2
echo "$prog: to install it for real (a Guardian proposal that root must approve): nexus try keep $attr" >&2
exit "$status"
