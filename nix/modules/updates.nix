# Security updates for the system itself, delivered as Guardian changes
# (docs/GUARDIAN-DEPLOYMENT.md, "Security updates"). Flathub apps update on
# their own (apps.nix); this is for everything from nixpkgs: the kernel,
# Firefox, Chromium, the desktop and its libraries.
#
#   nexus-update-check  root, weekly once online (and when the person asks
#                       Nexus): moves only NexusOS's nixpkgs input forward on
#                       its release branch, in a copy of /etc/nixos, and records
#                       the new flake.lock in /var/lib/nexus-updates
#   Guardian            its builder builds and VM-tests the host configuration
#                       with that lock and signs what changed; the person
#                       approves it with their password; it is used from the next
#                       restart as a one-time trial, and only a confirmed trial
#                       copies the lock into /etc/nixos
#
# NexusOS's own source (/etc/nixos/nexusos) is never changed.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.updates;
  guardian = config.services.nexus-guardian;
  # The builder writes this file when the person asks Nexus to check now
  # (local_builder.py check-updates); the path unit below starts the check.
  request = "/var/lib/nexus-guardian-builder/work/update-requested";
  waitOnline = lib.optionalString config.networking.networkmanager.enable
    " --wait-online ${pkgs.networkmanager}/bin/nm-online";
in
{
  options.nexusos.updates = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Look for security updates weekly and offer them through Guardian.";
    };
    minFreeGiB = lib.mkOption {
      type = lib.types.ints.between 1 1000;
      default = 10;
      description = "Free space /nix needs before an update is fetched or built.";
    };
  };

  config = lib.mkIf (cfg.enable && guardian.enable && guardian.localBuilder.enable) {
    systemd.services.nexus-update-check = {
      description = "NexusOS: look for security updates and hand them to Guardian";
      after = [ "network-online.target" "nix-daemon.socket" ];
      wants = [ "network-online.target" ];
      path = [ config.nix.package ];
      environment = { NIX_REMOTE = "daemon"; HOME = "/var/lib/nexus-updates"; };
      serviceConfig = {
        Type = "oneshot";
        ExecStart = "${guardian.package}/bin/nexus-update-check --dir /var/lib/nexus-updates --host-flake /etc/nixos"
          + " --min-free-gib ${toString cfg.minFreeGiB}" + waitOnline;
        # 75: offline; try again in an hour rather than waiting a week.
        RestartForceExitStatus = "75";
        RestartSec = "1h";
        TimeoutStartSec = "2h";
        StateDirectory = "nexus-updates";
        StateDirectoryMode = "0700";
        UMask = "0077";
        # In the background: never in the way of what the person is doing.
        Nice = 19;
        CPUSchedulingPolicy = "idle";
        IOSchedulingClass = "idle";
        NoNewPrivileges = true;
        PrivateTmp = true;
        ProtectSystem = "strict";
        ProtectHome = true;
        ReadWritePaths = [ "/var/lib/nexus-updates" ];
      };
    };
    systemd.timers.nexus-update-check = {
      wantedBy = [ "timers.target" ];
      timerConfig = { OnCalendar = "weekly"; Persistent = true; RandomizedDelaySec = "1d"; };
    };
    systemd.paths.nexus-update-check = {
      wantedBy = [ "multi-user.target" ];
      after = [ "nexus-guardian-builder.service" ];
      pathConfig = { PathChanged = request; Unit = "nexus-update-check.service"; };
    };
  };
}
