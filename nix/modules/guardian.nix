# NixOS module for the root-owned Guardian review broker (ADR-002).
{ config, lib, pkgs, ... }:
let
  cfg = config.services.nexus-guardian;
  configFile = pkgs.writeText "nexus-guardian-config.json" (builtins.toJSON {
    stateDir = "/var/lib/nexus-guardian";
    socket = "/run/nexus-guardian/review.sock";
    socketGroup = "nexus-guardian";
    allowedUsers = cfg.allowedUsers;
    hostConfig = cfg.hostConfig;
    trustedKeys = "/etc/nexus-guardian/trusted-keys";
  });
in
{
  options.services.nexus-guardian = {
    enable = lib.mkEnableOption "the NexusOS Guardian review broker";
    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.callPackage ../packages/nexus-guardian.nix { };
      description = "Guardian package providing the broker and approval console.";
    };
    allowedUsers = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ ];
      description = "Users whose Nexus Core may submit and review candidates. Root is never a client.";
    };
    hostConfig = lib.mkOption {
      type = lib.types.strMatching "[A-Za-z0-9+._:/?=&-]+#nixosConfigurations\\.[A-Za-z0-9_-]+";
      description = "Flake reference of this host's configuration, e.g. path:/etc/nixos#nixosConfigurations.host.";
    };
    trustedBuilderKeys = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      description = "Base64 Ed25519 public keys of builders whose attestations are trusted.";
    };
    staging = {
      enable = lib.mkEnableOption ''
        one-time trial boots of broker-approved systems (GRUB). Failed boots reset
        automatically and GRUB returns to the previous generation; a post-boot
        check confirms healthy trials or restores the baseline'';
      requiredUnits = lib.mkOption {
        type = lib.types.listOf lib.types.str;
        default = [ ];
        description = "Units that must be active for a trial boot to be confirmed.";
      };
      panicRebootSeconds = lib.mkOption {
        type = lib.types.ints.between 1 300;
        default = 10;
        description = "Reboot this many seconds after a kernel panic, so a failed boot falls back unattended.";
      };
    };
  };

  config = lib.mkIf cfg.enable {
    assertions = [
      { assertion = cfg.trustedBuilderKeys != [ ]; message = "services.nexus-guardian.trustedBuilderKeys must not be empty"; }
      { assertion = !(lib.elem "root" cfg.allowedUsers); message = "root cannot be a Guardian broker client"; }
    ];
    users.groups.nexus-guardian = { };
    # Group membership only grants reaching the socket; the broker still
    # checks each caller's kernel-reported UID against allowedUsers.
    users.users = lib.genAttrs cfg.allowedUsers (_: { extraGroups = [ "nexus-guardian" ]; });
    environment.systemPackages = [ cfg.package ];
    environment.etc."nexus-guardian/config.json" = { source = configFile; mode = "0444"; };
    environment.etc."nexus-guardian/trusted-keys" = {
      text = lib.concatMapStrings (key: key + "\n") cfg.trustedBuilderKeys;
      mode = "0444";
    };

    boot.loader.grub.default = lib.mkIf cfg.staging.enable "saved";
    boot.kernelParams = lib.mkIf cfg.staging.enable [
      "panic=${toString cfg.staging.panicRebootSeconds}"
      "boot.panic_on_fail"
    ];
    systemd.services.nexus-guardian-boot-check = lib.mkIf cfg.staging.enable {
      description = "NexusOS Guardian trial-boot assessment";
      wantedBy = [ "multi-user.target" ];
      after = [ "multi-user.target" "nix-daemon.socket" ];
      serviceConfig = {
        Type = "oneshot";
        RemainAfterExit = true;
        ExecStart = "${cfg.package}/bin/nexus-guardian-boot-check"
          + lib.concatMapStrings (unit: " --require-unit " + lib.escapeShellArg unit) cfg.staging.requiredUnits;
        StateDirectory = "nexus-guardian";
        StateDirectoryMode = "0700";
      };
    };

    systemd.services.nexus-guardian = {
      description = "NexusOS Guardian review broker (decides; never activates)";
      wantedBy = [ "multi-user.target" ];
      after = [ "local-fs.target" "nix-daemon.socket" ];
      path = [ config.nix.package ];
      environment.NIX_REMOTE = "daemon";
      serviceConfig = {
        Type = "simple";
        User = "root";
        Group = "nexus-guardian";
        ExecStart = "${cfg.package}/bin/nexus-guardian serve --config /etc/nexus-guardian/config.json";
        Restart = "on-failure";
        RestartSec = 2;
        UMask = "0077";
        StateDirectory = "nexus-guardian";
        StateDirectoryMode = "0700";
        RuntimeDirectory = "nexus-guardian";
        RuntimeDirectoryMode = "0750";
        NoNewPrivileges = true;
        CapabilityBoundingSet = "";
        AmbientCapabilities = "";
        PrivateTmp = true;
        PrivateDevices = true;
        PrivateNetwork = true;
        ProtectSystem = "strict";
        ProtectHome = true;
        ProtectKernelTunables = true;
        ProtectKernelModules = true;
        ProtectKernelLogs = true;
        ProtectControlGroups = true;
        ProtectClock = true;
        ProtectHostname = true;
        LockPersonality = true;
        RestrictSUIDSGID = true;
        RestrictRealtime = true;
        RestrictNamespaces = true;
        RemoveIPC = true;
        SystemCallArchitectures = "native";
        RestrictAddressFamilies = [ "AF_UNIX" ];
        IPAddressDeny = "any";
        ReadWritePaths = [ "/var/lib/nexus-guardian" "/run/nexus-guardian" ];
        LimitNOFILE = 64;
        TasksMax = 16;
        MemoryMax = "256M";
      };
    };
  };
}
