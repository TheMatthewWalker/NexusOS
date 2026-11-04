# nexus-install: put NexusOS on this computer's disk.
#
# Interactive by default. Every question can be answered with a flag instead,
# which is how the VM test drives it:
#   nexus-install --disk /dev/vdb --user alice --password-file pw.txt --yes
#
# What it does: erases the chosen disk, makes a 1 GiB EFI partition and an
# ext4 root, writes a small flake in /etc/nixos that imports a copy of the
# NexusOS source in /etc/nixos/nexusos, and runs nixos-install.
set -euo pipefail

source_dir=/etc/nexusos
disk="" user="" full_name="" password_file="" host="nexus" timezone="" assume_yes=0 extra_module="" nvidia=auto model="" encrypt=1

usage() {
  cat <<'EOF'
Usage: nexus-install [options]
  --disk DEVICE         disk to erase and install to, for example /dev/nvme0n1
  --user NAME           your login name (lower case letters, digits, - and _)
  --full-name "NAME"    shown on the login screen
  --password-file FILE  read the password from FILE instead of asking
  --hostname NAME       computer name (default: nexus)
  --timezone ZONE       for example Europe/London (default: detected, else UTC)
  --source DIR          NexusOS source to install (default: /etc/nexusos)
  --extra-module FILE   also include this NixOS module (used by the VM test)
  --no-nvidia           do not set up the NVIDIA driver, even if a card is found
  --model NAME:TAG      the local AI model (default: chosen for this computer)
  --no-encrypt          do not encrypt the disk (by default your password unlocks it at start-up)
  --yes                 do not ask for confirmation before erasing the disk
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --disk) disk="$2"; shift 2 ;;
    --user) user="$2"; shift 2 ;;
    --full-name) full_name="$2"; shift 2 ;;
    --password-file) password_file="$2"; shift 2 ;;
    --hostname) host="$2"; shift 2 ;;
    --timezone) timezone="$2"; shift 2 ;;
    --source) source_dir="$2"; shift 2 ;;
    --extra-module) extra_module="$2"; shift 2 ;;
    --no-nvidia) nvidia=off; shift ;;
    --model) model="$2"; shift 2 ;;
    --no-encrypt) encrypt=0; shift ;;
    --yes) assume_yes=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# Everything the installer prints also goes to a log, so a failure can be
# reported in full; the installed system keeps a copy in /var/log.
log=/tmp/nexus-install.log
exec > >(tee -a "$log") 2>&1
echo "nexus-install started $(date -Is)" >> "$log"
trap 'printf "\nnexus-install stopped: \"%s\" failed (line %s).\nThe full log is in %s\n" "$BASH_COMMAND" "$LINENO" "$log" >&2' ERR

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
fail() { printf '\nnexus-install: %s\nThe full log is in %s\n' "$*" "$log" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "run me as root: sudo nexus-install"
[ -d /sys/firmware/efi ] || fail "this computer booted without UEFI. NexusOS needs UEFI: turn off legacy/CSM boot in the firmware settings and boot the installer again."
[ -f "$source_dir/flake.nix" ] || fail "no NexusOS source in $source_dir"

say "NexusOS installer"
echo "This erases one disk and installs NexusOS on it. Other disks are not touched."

# --- Disk -------------------------------------------------------------------
if [ -z "$disk" ]; then
  echo
  echo "Disks on this computer:"
  lsblk -dpno NAME,SIZE,MODEL,TRAN -e 7,11 | sed 's/^/  /'
  echo
  read -rp "Disk to erase and install to (for example /dev/nvme0n1): " disk
fi
[ -b "$disk" ] || fail "$disk is not a disk"
[ "$(lsblk -dno TYPE "$disk")" = disk ] || fail "$disk is a partition; give the whole disk"
# A failed attempt leaves this disk mounted at /mnt: release that first, so
# trying again works. Mounted anywhere else, the disk is really in use.
if findmnt -rno SOURCE,TARGET | awk -v d="$disk" 'index($1, d) == 1 && $2 ~ "^/mnt(/|$)"' | grep -q .; then
  echo "Unmounting what an earlier attempt left mounted at /mnt."
  umount -R /mnt || fail "could not unmount /mnt from the earlier attempt; restart the computer and try again"
fi
if findmnt -rno SOURCE | grep -q "^$disk"; then
  fail "$disk is in use (mounted). Is it the disk the installer is running from?"
fi

# --- Person -----------------------------------------------------------------
if [ -z "$user" ]; then
  read -rp "Your login name: " user
fi
[[ "$user" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || fail "login names use lower case letters, digits, - and _, and start with a letter"
case "$user" in root|nixos|nobody) fail "$user is reserved; pick another name" ;; esac
if [ -z "$full_name" ] && [ -z "$password_file" ]; then
  read -rp "Your full name (optional): " full_name
fi

if [ -n "$password_file" ]; then
  password="$(head -n1 "$password_file")"
else
  while true; do
    read -rsp "Password: " password; echo
    read -rsp "Password again: " again; echo
    if [ "$password" != "$again" ]; then echo "The passwords did not match. Try again."
    elif [ ${#password} -lt 10 ]; then echo "Use at least 10 characters: it also approves system changes."
    else break; fi
  done
fi
[ ${#password} -ge 10 ] || fail "use a password of at least 10 characters"

[[ "$host" =~ ^[A-Za-z0-9][A-Za-z0-9-]{0,62}$ ]] || fail "computer names use letters, digits and -"
if [ -z "$timezone" ]; then
  timezone="$(timedatectl show -p Timezone --value 2>/dev/null || true)"
  [ -n "$timezone" ] || timezone=UTC
fi
[ -e "/etc/zoneinfo/$timezone" ] || [ "$timezone" = UTC ] || fail "unknown time zone $timezone"

# --- Hardware ---------------------------------------------------------------
# Found here, written to local.nix, acted on by nix/modules/hardware.nix and
# nix/modules/nvidia.nix: nobody picks drivers by hand.
#
# An NVIDIA card needs its own driver. Laptops that also have a built-in Intel
# or AMD GPU use PRIME offload, which needs both cards' PCI bus IDs.
# lspci -Dnn lines look like:
#   0000:01:00.0 VGA compatible controller [0300]: NVIDIA Corporation AD107M [GeForce RTX 4060 Max-Q / Mobile] [10de:28a0] (rev a1)
nvidia_lines="" nvidia_bus="" intel_bus="" amd_bus="" nvidia_name="" nvidia_open=true nvidia_driver=stable
bus_id() {  # 0000:01:00.0 -> PCI:1:0:0 (decimal, as NixOS expects)
  local slot="${1#*:}" bus dev fn
  bus="${slot%%:*}"; dev="${slot#*:}"; fn="${dev#*.}"; dev="${dev%.*}"
  printf 'PCI:%d:%d:%d' "$((16#$bus))" "$((16#$dev))" "$((16#$fn))"
}
# Where the scan reads from; the tests (tests/installer) point these at recordings.
proc="${NEXUS_PROC:-/proc}" sys="${NEXUS_SYS:-/sys}"
gpu_name() { sed -E 's/^[^ ]+ [^:]+: (.*) \[[0-9a-f]{4}:[0-9a-f]{4}\].*/\1/' <<<"$1"; }
gpus="$(lspci -Dnn 2>/dev/null | grep -E '\[(0300|0302|0380)\]' || true)"
intel="$(grep -E '\[8086:[0-9a-f]{4}\]' <<<"$gpus" | head -n1 || true)"
amd="$(grep -E '\[1002:[0-9a-f]{4}\]' <<<"$gpus" | head -n1 || true)"
if [ "$nvidia" = auto ]; then
  nvidia_lines="$(grep -E '\[10de:[0-9a-f]{4}\]' <<<"$gpus" || true)"
  if [ -n "$nvidia_lines" ]; then
    first="$(head -n1 <<<"$nvidia_lines")"
    nvidia_bus="$(bus_id "${first%% *}")"
    nvidia_name="$(sed -E 's/.*\[([^]]*)\] \[10de:[0-9a-f]{4}\].*/\1/' <<<"$first")"
    device="$(grep -oE '\[10de:[0-9a-f]{4}\]' <<<"$first" | cut -c7-10)"
    # NVIDIA's open modules and current driver need Turing (device 0x1e00) or newer.
    if [ "$((16#$device))" -lt "$((16#1e00))" ]; then nvidia_open=false nvidia_driver=legacy_580; fi
    [ -n "$intel" ] && intel_bus="$(bus_id "${intel%% *}")"
    [ -n "$amd" ] && amd_bus="$(bus_id "${amd%% *}")"
  fi
fi
# An AMD GPU's own memory (amdgpu reports it; built-in ones show a small carve-out).
amd_vram_gib=0
for f in "$sys"/class/drm/card*/device/mem_info_vram_total; do
  [ -r "$f" ] || continue
  grep -q 0x1002 "$(dirname "$f")/vendor" 2>/dev/null || continue
  v=$(( $(cat "$f") / 1073741824 )); [ "$v" -gt "$amd_vram_gib" ] && amd_vram_gib=$v
done
if [ -n "$nvidia_lines" ]; then gpu=nvidia
elif [ -n "$amd" ]; then gpu=amd
elif [ -n "$intel" ]; then gpu=intel
else gpu=none
fi
case "$(grep -m1 '^vendor_id' "$proc/cpuinfo" 2>/dev/null | awk '{print $3}')" in
  GenuineIntel) cpu=intel ;; AuthenticAMD) cpu=amd ;; *) cpu=other ;;
esac
cpu_name="$(grep -m1 '^model name' "$proc/cpuinfo" 2>/dev/null | cut -d: -f2- | sed 's/^ *//')"
memory_gib=$(( ($(awk '/^MemTotal:/ {print $2}' "$proc/meminfo") + 524288) / 1048576 ))
# A battery, or a portable chassis type (SMBIOS: 8-10 portable/laptop/notebook,
# 14 sub-notebook, 31 convertible, 32 detachable).
laptop=false
if ls "$sys"/class/power_supply/BAT* > /dev/null 2>&1; then laptop=true
else
  case "$(cat "$sys/class/dmi/id/chassis_type" 2>/dev/null)" in 8|9|10|14|31|32) laptop=true ;; esac
fi
# The model nix/modules/hardware.nix chooses for this computer (kept in step with it).
if [ "$gpu" = nvidia ] || { [ "$gpu" = amd ] && [ "$amd_vram_gib" -ge 6 ]; }; then ai_model="qwen3:8b" ai_where="on the graphics card"
elif [ "$memory_gib" -ge 8 ]; then ai_model="qwen3:4b" ai_where="on the processor"
else ai_model="qwen3:1.7b" ai_where="on the processor"
fi
if [ -n "$model" ]; then
  [[ "$model" =~ ^[a-z0-9][a-z0-9._-]*:[a-z0-9._-]+$ ]] || fail "--model takes a library model as name:tag, for example qwen3:4b"
  ai_model="$model" ai_where="as asked"
fi
models_dir="${NEXUS_MODELS:-/etc/nexusos-model}/manifests/registry.ollama.ai/library"
ai_manifest="$models_dir/${ai_model%%:*}/${ai_model#*:}"
if [ -f "$ai_manifest" ]; then ai_where="$ai_where, from the USB stick"
else ai_where="$ai_where, downloaded once the computer is online"
fi

if [ -n "$nvidia_lines" ] && [ -n "$intel_bus$amd_bus" ]; then graphics="$nvidia_name, with the built-in GPU running the desktop (PRIME offload)"
elif [ -n "$nvidia_lines" ]; then graphics="$nvidia_name"
elif [ -n "$amd" ]; then graphics="$(gpu_name "$amd") (built-in drivers)"
elif [ -n "$intel" ]; then graphics="$(gpu_name "$intel") (built-in drivers)"
else graphics="basic display (no graphics processor found)"
fi
[ "$nvidia" = off ] && [ -n "$(grep -E '\[10de:' <<<"$gpus" || true)" ] && graphics="$graphics; NVIDIA card left without its driver (--no-nvidia)"

# --- Confirm ----------------------------------------------------------------
say "Ready to install"
echo "  Disk:      $disk ($(lsblk -dno SIZE "$disk")) — everything on it will be erased"
if [ "$encrypt" -eq 1 ]; then
  echo "  Encrypted: yes; your password unlocks the disk when the computer starts"
else
  echo "  Encrypted: no (--no-encrypt)"
fi
echo "  User:      $user"
echo "  Computer:  $host"
echo "  Time zone: $timezone"
echo "  Processor: ${cpu_name:-unknown}, ${memory_gib} GB memory$([ "$laptop" = true ] && echo ", laptop")"
echo "  Graphics:  $graphics"
echo "  Local AI:  $ai_model, $ai_where"
if [ "$assume_yes" -ne 1 ]; then
  echo
  read -rp "Type the disk name ($disk) to erase it and install: " answer
  [ "$answer" = "$disk" ] || fail "not confirmed; nothing was changed"
fi

# --- Partition and format ---------------------------------------------------
say "Preparing $disk"
umount -R /mnt 2>/dev/null || true
cryptsetup close nexusos-root 2>/dev/null || true
wipefs -af "$disk" > /dev/null
parted -s "$disk" -- mklabel gpt \
  mkpart ESP fat32 1MiB 1GiB set 1 esp on \
  mkpart nexusos ext4 1GiB 100%
# Wait until the kernel shows exactly the two new partitions.
parts=()
for _ in $(seq 1 30); do
  partprobe "$disk" 2>/dev/null || true
  udevadm settle
  mapfile -t parts < <(lsblk -lnpo NAME,TYPE "$disk" | awk '$2 == "part" { print $1 }')
  [ "${#parts[@]}" -eq 2 ] && [ -b "${parts[0]}" ] && [ -b "${parts[1]}" ] && break
  sleep 1
done
[ "${#parts[@]}" -eq 2 ] || fail "expected two partitions on $disk, found ${#parts[@]}"
boot_part="${parts[0]}" root_part="${parts[1]}"

# An earlier system on this disk can leave filesystem signatures inside the
# new partitions; wipe them so nothing mistakes a partition for its old self.
wipefs -af "$boot_part" "$root_part" > /dev/null
mkfs.fat -F 32 -n BOOT "$boot_part" > /dev/null
# Encryption: LUKS2 around the root filesystem, unlocked with the person's
# password before anything else starts. Without the password, a lost or
# stolen disk shows nothing: not the files, not Guardian's builder key.
# nixos-generate-config finds the encrypted partition and has the initrd ask.
root_fs="$root_part"
if [ "$encrypt" -eq 1 ]; then
  printf '%s' "$password" | cryptsetup luksFormat --type luks2 --batch-mode --label nexusos-crypt --key-file=- "$root_part"
  printf '%s' "$password" | cryptsetup open --key-file=- --allow-discards "$root_part" nexusos-root
  root_fs=/dev/mapper/nexusos-root
fi
mkfs.ext4 -q -F -L nexusos "$root_fs"
udevadm settle
[ "$(blkid -p -o value -s TYPE "$boot_part")" = vfat ] || fail "$boot_part did not format as FAT"
[ "$(blkid -p -o value -s TYPE "$root_fs")" = ext4 ] || fail "$root_fs did not format as ext4"
mount -t ext4 "$root_fs" /mnt
mkdir -p /mnt/boot
mount -t vfat -o umask=0077 "$boot_part" /mnt/boot

# --- Configuration ----------------------------------------------------------
say "Writing the configuration"
nixos-generate-config --root /mnt > /dev/null
rm -f /mnt/etc/nixos/configuration.nix
# The NexusOS source lives inside /etc/nixos, so the machine's flake can
# refer to it by a relative path and rebuild without network access.
mkdir -p /mnt/etc/nixos/nexusos
cp -rL --no-preserve=ownership "$source_dir/." /mnt/etc/nixos/nexusos/
chmod -R u+w /mnt/etc/nixos/nexusos

nix_string() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/\$/\\$/g')"; }

modules="./hardware-configuration.nix ./local.nix"
if [ -n "$extra_module" ]; then
  [ -f "$extra_module" ] || fail "no module at $extra_module"
  cp "$extra_module" /mnt/etc/nixos/extra.nix
  modules="$modules ./extra.nix"
fi
cat > /mnt/etc/nixos/flake.nix <<EOF
# This computer's NexusOS configuration. Change local.nix, then run:
#   sudo nixos-rebuild switch --flake /etc/nixos#nexus
{
  inputs.nexusos.url = "path:./nexusos";
  outputs = { self, nexusos }: {
    nixosConfigurations.nexus = nexusos.lib.mkSystem {
      modules = [ $modules ];
    };
  };
}
EOF
cat > /mnt/etc/nixos/local.nix <<EOF
# Settings for this computer, written by nexus-install. Options are listed
# in /etc/nixos/nexusos/nix/profiles/nexusos.nix and nix/modules/desktop.nix.
{
  nexusos.user = $(nix_string "$user");
  nexusos.fullName = $(nix_string "$full_name");
  networking.hostName = $(nix_string "$host");
  time.timeZone = $(nix_string "$timezone");
}
EOF
# What the hardware scan found (nix/modules/hardware.nix), before the closing brace.
sed -i '$d' /mnt/etc/nixos/local.nix
{
  echo "  # Found by nexus-install: ${cpu_name:-processor}; $graphics"
  echo "  nexusos.hardware.cpu = \"$cpu\";"
  echo "  nexusos.hardware.gpu = \"$gpu\";"
  [ "$amd_vram_gib" -eq 0 ] || echo "  nexusos.hardware.gpuMemoryGiB = $amd_vram_gib;"
  echo "  nexusos.hardware.memoryGiB = $memory_gib;"
  echo "  nexusos.hardware.laptop = $laptop;"
  [ -z "$model" ] || echo "  services.nexus-core.ollamaModel = \"$model\";"
  if [ -n "$nvidia_lines" ]; then
    echo "  nexusos.nvidia.enable = true;"
    [ "$nvidia_open" = true ] || echo "  nexusos.nvidia.open = false;"
    [ "$nvidia_driver" = stable ] || echo "  nexusos.nvidia.driver = \"$nvidia_driver\";"
    if [ -n "$intel_bus$amd_bus" ]; then
      echo "  nexusos.nvidia.prime.nvidiaBusId = \"$nvidia_bus\";"
      [ -z "$intel_bus" ] || echo "  nexusos.nvidia.prime.intelBusId = \"$intel_bus\";"
      [ -z "$amd_bus" ] || echo "  nexusos.nvidia.prime.amdgpuBusId = \"$amd_bus\";"
    fi
  fi
  echo "}"
} >> /mnt/etc/nixos/local.nix

# --- Install ----------------------------------------------------------------
say "Installing NexusOS (this takes a while)"
# Everything the install needs is on the image. Online, the NixOS cache can
# still save some building; offline, give up on it quickly. (Do not empty
# the substituters: nixos-install copies from this USB stick's store through
# one, and an empty list would make it download nixpkgs from GitHub.)
offline=()
if ! curl -sfI --max-time 8 https://cache.nixos.org/nix-cache-info > /dev/null 2>&1; then
  echo "No internet connection: installing from the USB stick only."
  offline=(--option connect-timeout 3)
fi
# Lock first: locking inside nixos-install would change the flake mid-build.
nix --extra-experimental-features "nix-command flakes" flake lock "path:/mnt/etc/nixos" "${offline[@]}"
# Offline, only small files specific to this computer may need building (its
# boot files and settings). Anything bigger means a package is missing from
# the stick: say which, rather than spend hours trying to compile it.
if [ "${#offline[@]}" -gt 0 ]; then
  plan="$(nix --extra-experimental-features "nix-command flakes" build --dry-run "${offline[@]}" \
    "path:/mnt/etc/nixos#nixosConfigurations.nexus.config.system.build.toplevel" 2>&1 || true)"
  heavy="$(sed -n '/will be built/,/will be fetched/p' <<<"$plan" | grep -oE '/nix/store/[a-z0-9]{32}-[^ ]+\.drv' \
    | sed -E 's|/nix/store/[a-z0-9]{32}-||; s|\.drv$||' \
    | grep -E '^(stage0|hex0|bootstrap|gcc|binutils|glibc-2|xgcc|bash-5|python3-3|perl-5\.[0-9.]+$|rustc|cargo|llvm|clang)' | sort -u | head -20 || true)"
  if [ -n "$heavy" ]; then
    printf '%s\n' "$plan" >> "$log"
    fail "this computer's system needs packages the USB stick does not carry, and there is no internet to fetch them:
  ${heavy//$'\n'/$'\n'  }
Connect to the internet (Wi-Fi in the system panel) and try again, or report this list."
  fi
fi
nixos-install --root /mnt --flake "path:/mnt/etc/nixos#nexus" --no-root-passwd --no-channel-copy "${offline[@]}"

# The local model, if the image carries the one chosen above: first boot
# hands it to Ollama (nexus-model-pull), so Nexus answers without
# downloading it. Only that model's manifest and files are copied.
if [ -f "$ai_manifest" ]; then
  say "Copying the local AI model ($ai_model)"
  seed=/mnt/var/lib/nexus-model-seed
  rel="${ai_manifest#/etc/nexusos-model/}"
  mkdir -p "$seed/blobs" "$(dirname "$seed/$rel")"
  cp -L --no-preserve=ownership,mode "$ai_manifest" "$seed/$rel"
  grep -oE 'sha256:[0-9a-f]{64}' "$ai_manifest" | sort -u | while read -r digest; do
    cp -L --no-preserve=ownership,mode "/etc/nexusos-model/blobs/${digest/:/-}" "$seed/blobs/"
  done
fi

say "Setting your password"
printf '%s:%s\n' "$user" "$password" | nixos-enter --root /mnt -c 'chpasswd'

mkdir -p /mnt/var/log && cp "$log" /mnt/var/log/nexus-install.log
sync
say "NexusOS is installed"
echo "Remove the installer USB stick, then restart the computer."
