# Hibernate (Settings, Power), off until turned on through Guardian
# (nexusos.hibernate.enable). The memory is written to a swap file the size
# of the memory, on the (encrypted) system disk, and the computer turns off;
# starting it again unlocks the disk as usual and carries on where it was.
# The file is made, or grown after a memory upgrade, at start-up when there
# is room for it and 5 GB to spare. systemd (255 and later) records where it
# is in an EFI variable when hibernating, and the systemd initrd resumes
# from it, so no resume= kernel options are needed. zram stays the swap
# used day to day: the file has the lowest priority.
{ config, lib, pkgs, ... }:
let
  swapFile = "/var/lib/nexus/hibernate.swap";
in
{
  options.nexusos.hibernate.enable = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "Hibernate: save everything to the disk and turn off, carrying on where you were at the next start.";
  };

  config = lib.mkIf config.nexusos.hibernate.enable {
    boot.initrd.systemd.enable = lib.mkDefault true;
    systemd.services.nexus-hibernate-swap = {
      description = "Hibernate: a swap file the size of the memory";
      wantedBy = [ "multi-user.target" ];
      path = with pkgs; [ coreutils util-linux gawk e2fsprogs ];
      serviceConfig = { Type = "oneshot"; RemainAfterExit = true; };
      script = ''
        set -eu
        f=${swapFile}
        mkdir -p "$(dirname "$f")"
        # The memory, plus a tenth for what the kernel needs alongside it, in MiB.
        want=$(awk '/^MemTotal:/ { printf "%d", $2 * 1.1 / 1024 + 1 }' /proc/meminfo)
        have=0
        [ -f "$f" ] && have=$(( $(stat -c %s "$f") / 1048576 ))
        if [ "$have" -lt "$want" ]; then
          free=$(df -Pm "$(dirname "$f")" | awk 'NR == 2 { print $4 }')
          if [ $(( free + have )) -lt $(( want + 5120 )) ]; then
            echo "nexus-hibernate: not enough room for a $want MiB swap file; hibernate stays unavailable." >&2
            exit 0
          fi
          swapoff "$f" 2>/dev/null || true
          rm -f "$f"
          fallocate -l "''${want}M" "$f"
          chmod 600 "$f"
          mkswap -q "$f"
        fi
        grep -q "^$f " /proc/swaps || swapon -p 0 "$f"
      '';
    };
  };
}
