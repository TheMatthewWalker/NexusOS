# Firmware updates for the computer itself (UEFI/BIOS, Thunderbolt, docks,
# SSDs, keyboards, mice) from the Linux Vendor Firmware Service, with fwupd.
#  - fwupd's own timer refreshes the list of firmware daily.
#  - A user timer looks once a day and says so with a notification when there
#    is something to install; Settings, Updates installs it. fwupd asks for an
#    administrator's password (polkit) before it writes firmware.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.firmware;
  notify = pkgs.writeShellScript "nexus-firmware-notify" ''
    count=$(${config.services.fwupd.package}/bin/fwupdmgr get-updates --json 2>/dev/null \
      | ${pkgs.jq}/bin/jq '[.Devices[]? | select((.Releases // []) | length > 0)] | length' 2>/dev/null)
    case "$count" in ""|0) exit 0 ;; esac
    stamp="''${XDG_STATE_HOME:-$HOME/.local/state}/nexusos/firmware-notified"
    mkdir -p "$(dirname "$stamp")"
    # Say it once per set of updates, not every day.
    seen=$(cat "$stamp" 2>/dev/null)
    [ "$seen" = "$count" ] && exit 0
    echo "$count" > "$stamp"
    ${pkgs.libnotify}/bin/notify-send --app-name=Updates --icon=system-software-update \
      "Firmware update ready" "Open Settings, Updates to install it."
  '';
in
{
  options.nexusos.firmware.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Firmware updates with fwupd, offered in Settings, Updates.";
  };

  config = lib.mkIf cfg.enable {
    services.fwupd.enable = lib.mkDefault true;
    systemd.user.services.nexus-firmware-notify = {
      description = "Say when a firmware update is ready";
      serviceConfig = { Type = "oneshot"; ExecStart = notify; };
    };
    systemd.user.timers.nexus-firmware-notify = {
      wantedBy = [ "timers.target" ];
      timerConfig = { OnStartupSec = "30min"; OnUnitActiveSec = "1d"; RandomizedDelaySec = "30min"; };
    };
  };
}
