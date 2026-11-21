# Settings, Backups: the person's files, backed up to a drive once a day.
# scripts/backup/nexus_home_backup.py runs as the person (never as root) with
# restic: an encrypted, deduplicated backup on the drive, so each day only
# adds what changed and nothing on the drive can be read without the backup
# password. A user timer runs `nexus-backup auto` every hour; it backs up when
# a day has passed and the drive is plugged in (opening it if it is not yet),
# and says so with a notification if it cannot finish.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.backups;
  nexus-backup = pkgs.writeShellScriptBin "nexus-backup" ''
    export PATH=${lib.makeBinPath [ pkgs.restic pkgs.util-linux pkgs.udisks pkgs.libnotify ]}:$PATH
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/backup/nexus_home_backup.py} "$@"
  '';
in
{
  options.nexusos.backups.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Settings, Backups, and the daily backup to a drive once the person sets one up.";
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ nexus-backup pkgs.restic ];
    services.udisks2.enable = lib.mkDefault true;

    systemd.user.services.nexus-backup = {
      description = "Back up your files to the backup drive (Settings, Backups)";
      serviceConfig = {
        Type = "oneshot";
        ExecStart = "${nexus-backup}/bin/nexus-backup auto";
        Nice = 10;
        IOSchedulingClass = "idle";
      };
    };
    systemd.user.timers.nexus-backup = {
      description = "Back up your files once a day, when the backup drive is there";
      wantedBy = [ "timers.target" ];
      timerConfig = {
        OnStartupSec = "10min";
        OnUnitActiveSec = "1h";
        RandomizedDelaySec = "5min";
      };
    };
  };
}
