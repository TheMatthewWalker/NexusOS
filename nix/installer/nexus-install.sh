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
disk="" user="" full_name="" password_file="" host="nexus" timezone="" assume_yes=0 extra_module="" nvidia=auto

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
    --yes) assume_yes=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
fail() { printf '\nnexus-install: %s\n' "$*" >&2; exit 1; }

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
    [ "$password" = "$again" ] && break
    echo "The passwords did not match. Try again."
  done
fi
[ ${#password} -ge 6 ] || fail "use a password of at least 6 characters"

[[ "$host" =~ ^[A-Za-z0-9][A-Za-z0-9-]{0,62}$ ]] || fail "computer names use letters, digits and -"
if [ -z "$timezone" ]; then
  timezone="$(timedatectl show -p Timezone --value 2>/dev/null || true)"
  [ -n "$timezone" ] || timezone=UTC
fi
[ -e "/etc/zoneinfo/$timezone" ] || [ "$timezone" = UTC ] || fail "unknown time zone $timezone"

# --- Graphics ---------------------------------------------------------------
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
if [ "$nvidia" = auto ]; then
  gpus="$(lspci -Dnn | grep -E '\[(0300|0302|0380)\]' || true)"
  nvidia_lines="$(grep -E '\[10de:[0-9a-f]{4}\]' <<<"$gpus" || true)"
  if [ -n "$nvidia_lines" ]; then
    first="$(head -n1 <<<"$nvidia_lines")"
    nvidia_bus="$(bus_id "${first%% *}")"
    nvidia_name="$(sed -E 's/.*\[([^]]*)\] \[10de:[0-9a-f]{4}\].*/\1/' <<<"$first")"
    device="$(grep -oE '\[10de:[0-9a-f]{4}\]' <<<"$first" | cut -c7-10)"
    # NVIDIA's open modules and current driver need Turing (device 0x1e00) or newer.
    if [ "$((16#$device))" -lt "$((16#1e00))" ]; then nvidia_open=false nvidia_driver=legacy_580; fi
    intel="$(grep -E '\[8086:[0-9a-f]{4}\]' <<<"$gpus" | head -n1 || true)"
    amd="$(grep -E '\[1002:[0-9a-f]{4}\]' <<<"$gpus" | head -n1 || true)"
    [ -n "$intel" ] && intel_bus="$(bus_id "${intel%% *}")"
    [ -n "$amd" ] && amd_bus="$(bus_id "${amd%% *}")"
  fi
fi
if [ -z "$nvidia_lines" ]; then graphics="no NVIDIA card (built-in drivers)"
elif [ -n "$intel_bus$amd_bus" ]; then graphics="$nvidia_name, with the built-in GPU running the desktop (PRIME offload)"
else graphics="$nvidia_name"
fi

# --- Confirm ----------------------------------------------------------------
say "Ready to install"
echo "  Disk:      $disk ($(lsblk -dno SIZE "$disk")) — everything on it will be erased"
echo "  User:      $user"
echo "  Computer:  $host"
echo "  Time zone: $timezone"
echo "  Graphics:  $graphics"
if [ "$assume_yes" -ne 1 ]; then
  echo
  read -rp "Type the disk name ($disk) to erase it and install: " answer
  [ "$answer" = "$disk" ] || fail "not confirmed; nothing was changed"
fi

# --- Partition and format ---------------------------------------------------
say "Preparing $disk"
umount -R /mnt 2>/dev/null || true
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
mkfs.ext4 -q -F -L nexusos "$root_part"
udevadm settle
[ "$(blkid -p -o value -s TYPE "$boot_part")" = vfat ] || fail "$boot_part did not format as FAT"
[ "$(blkid -p -o value -s TYPE "$root_part")" = ext4 ] || fail "$root_part did not format as ext4"
mount -t ext4 "$root_part" /mnt
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
if [ -n "$nvidia_lines" ]; then
  # Insert before the closing brace of local.nix.
  sed -i '$d' /mnt/etc/nixos/local.nix
  {
    echo "  # Found by nexus-install: $nvidia_name"
    echo "  nexusos.nvidia.enable = true;"
    [ "$nvidia_open" = true ] || echo "  nexusos.nvidia.open = false;"
    [ "$nvidia_driver" = stable ] || echo "  nexusos.nvidia.driver = \"$nvidia_driver\";"
    if [ -n "$intel_bus$amd_bus" ]; then
      echo "  nexusos.nvidia.prime.nvidiaBusId = \"$nvidia_bus\";"
      [ -z "$intel_bus" ] || echo "  nexusos.nvidia.prime.intelBusId = \"$intel_bus\";"
      [ -z "$amd_bus" ] || echo "  nexusos.nvidia.prime.amdgpuBusId = \"$amd_bus\";"
    fi
    echo "}"
  } >> /mnt/etc/nixos/local.nix
fi

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
nixos-install --root /mnt --flake "path:/mnt/etc/nixos#nexus" --no-root-passwd --no-channel-copy "${offline[@]}"

# The local model, if the image carries one: first boot hands it to Ollama
# (nexus-model-pull), so Nexus answers without downloading it.
if [ -d /etc/nexusos-model/manifests ]; then
  say "Copying the local AI model"
  mkdir -p /mnt/var/lib/nexus-model-seed
  cp -rL --no-preserve=ownership,mode /etc/nexusos-model/. /mnt/var/lib/nexus-model-seed/
fi

say "Setting your password"
printf '%s:%s\n' "$user" "$password" | nixos-enter --root /mnt -c 'chpasswd'

sync
say "NexusOS is installed"
echo "Remove the installer USB stick, then restart the computer."
