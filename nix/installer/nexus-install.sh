# nexus-install: put NexusOS on this computer's disk.
#
# Interactive by default. Every question can be answered with a flag instead,
# which is how the VM test drives it:
#   nexus-install --disk /dev/vdb --user alice --password-file pw.txt --yes
#
# What it does: erases the chosen disk, makes a 1 GiB EFI partition and an
# ext4 root, copies the NexusOS source to /etc/nexusos, writes a small flake in
# /etc/nixos that imports it, and runs nixos-install.
set -euo pipefail

source_dir=/etc/nexusos
disk="" user="" full_name="" password_file="" host="nexus" timezone="" assume_yes=0

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

# --- Confirm ----------------------------------------------------------------
say "Ready to install"
echo "  Disk:      $disk ($(lsblk -dno SIZE "$disk")) — everything on it will be erased"
echo "  User:      $user"
echo "  Computer:  $host"
echo "  Time zone: $timezone"
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
partprobe "$disk" 2>/dev/null || true
udevadm settle
mapfile -t parts < <(lsblk -lnpo NAME,TYPE "$disk" | awk '$2 == "part" { print $1 }')
[ "${#parts[@]}" -eq 2 ] || fail "expected two partitions on $disk, found ${#parts[@]}"
boot_part="${parts[0]}" root_part="${parts[1]}"

mkfs.fat -F 32 -n BOOT "$boot_part" > /dev/null
mkfs.ext4 -q -F -L nexusos "$root_part"
mount "$root_part" /mnt
mkdir -p /mnt/boot
mount -o umask=0077 "$boot_part" /mnt/boot

# --- Configuration ----------------------------------------------------------
say "Writing the configuration"
nixos-generate-config --root /mnt > /dev/null
rm -f /mnt/etc/nixos/configuration.nix
mkdir -p /mnt/etc/nexusos
# Keep the executable bits: the flake lock hashes the source, and the copy
# must hash the same as the installer's own /etc/nexusos.
cp -r --no-preserve=ownership "$source_dir/." /mnt/etc/nexusos/
chmod -R u+w /mnt/etc/nexusos

nix_string() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/\$/\\$/g')"; }

cat > /mnt/etc/nixos/flake.nix <<'EOF'
# This computer's NexusOS configuration. Change local.nix, then run:
#   sudo nixos-rebuild switch --flake /etc/nixos#nexus
{
  inputs.nexusos.url = "path:/etc/nexusos";
  outputs = { self, nexusos }: {
    nixosConfigurations.nexus = nexusos.lib.mkSystem {
      modules = [ ./hardware-configuration.nix ./local.nix ];
    };
  };
}
EOF
cat > /mnt/etc/nixos/local.nix <<EOF
# Settings for this computer, written by nexus-install. Options are listed
# in /etc/nexusos/nix/profiles/nexusos.nix and nix/modules/desktop.nix.
{
  nexusos.user = $(nix_string "$user");
  nexusos.fullName = $(nix_string "$full_name");
  networking.hostName = $(nix_string "$host");
  time.timeZone = $(nix_string "$timezone");
}
EOF

# --- Install ----------------------------------------------------------------
say "Installing NexusOS (this takes a while)"
nixos-install --root /mnt --flake "path:/mnt/etc/nixos#nexus" --no-root-passwd --no-channel-copy

say "Setting your password"
printf '%s:%s\n' "$user" "$password" | nixos-enter --root /mnt -c 'chpasswd'

sync
say "NexusOS is installed"
echo "Remove the installer USB stick, then restart the computer."
