# nexus-windows-vm: Windows in a window, for programs that need Windows
# (Office desktop apps, Adobe, work tools). Not for games with anti-cheat:
# those refuse virtual machines; restart into Windows for them instead.
#   nexus-windows-vm          open Windows (start it, or set it up the first time)
#   nexus-windows-vm create   set it up: choose the Windows ISO (Microsoft's own download)
# It runs as the person (libvirt's session, qemu:///session): no password,
# its disk is in ~/.local/share/libvirt/images, and it can read the ISO
# where it was downloaded. It gets half the memory (up to 8 GB), half the
# processors, an 80 GB disk that only takes the space Windows uses, the TPM
# and UEFI that Windows 11 asks for, and the network through this computer.
set -eu
uri=qemu:///session
name=windows
say() { notify-send --app-name="Windows" "$1" "${2:-}"; }

exists() { virsh -c "$uri" dominfo "$name" >/dev/null 2>&1; }

case "${1:-open}" in
  open)
    if exists; then
      [ "$(virsh -c "$uri" domstate "$name")" = "running" ] || virsh -c "$uri" start "$name" >/dev/null
      exec virt-viewer --connect "$uri" --wait "$name"
    fi
    exec "$0" create ;;
  create)
    if exists; then say "Windows is already set up" "Open Windows from Apps."; exit 0; fi
    total=$(awk '/^MemTotal:/ { print int($2 / 1024) }' /proc/meminfo)
    if [ "$total" -lt 8000 ]; then
      zenity --error --title="Windows" --text="This computer has $((total / 1024)) GB of memory. Windows in a window needs a computer with 8 GB or more." || true
      exit 1
    fi
    memory=$((total / 2)); [ "$memory" -gt 8192 ] && memory=8192
    cpus=$(( $(nproc) / 2 )); [ "$cpus" -lt 2 ] && cpus=2
    free=$(df -Pk "$HOME" | awk 'NR == 2 { print int($4 / 1048576) }')
    if [ "$free" -lt 40 ]; then
      zenity --error --title="Windows" --text="Windows needs about 40 GB of free space; your home folder has ${free} GB." || true
      exit 1
    fi
    zenity --info --title="Set up Windows" --ok-label="Choose the ISO…" --text="You need Windows' installation file (an ISO) and a Windows licence.

Download the ISO from Microsoft: microsoft.com/software-download/windows11 (choose \"Download Windows 11 Disk Image (ISO)\"). Then choose it on the next step.

Windows gets $((memory / 1024)) GB of memory and $cpus processors while it runs. Games with anti-cheat do not work in it: use Restart into Windows for those." || exit 0
    iso=$(zenity --file-selection --title="Choose the Windows ISO" --file-filter="Windows ISO | *.iso *.ISO") || exit 0
    case "$iso" in *.iso|*.ISO) ;; *) say "That is not an ISO file"; exit 1 ;; esac
    say "Setting up Windows" "Its window opens in a moment: follow Windows' own set-up there."
    virt-install --connect "$uri" --name "$name" --osinfo win11 \
      --memory "$memory" --vcpus "$cpus" --cpu host-passthrough \
      --disk size=80,bus=sata,format=qcow2 --disk path="$iso",device=cdrom,bus=sata,readonly=on \
      --network user,model=e1000e \
      --tpm backend.type=emulator,backend.version=2.0,model=tpm-crb \
      --boot uefi --graphics spice --video qxl --sound ich9 \
      --noautoconsole >/dev/null
    exec virt-viewer --connect "$uri" --wait "$name" ;;
  *) echo "nexus-windows-vm [create]" >&2; exit 2 ;;
esac
