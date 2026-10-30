# NixOS module for the root-owned Guardian review broker (ADR-002), and the
# pieces that let a person use it from the desktop of their own computer:
#
#   localBuilder     this computer builds, VM-tests and signs candidates itself,
#                    with a key made at first boot (root only); the broker trusts it
#   desktopApproval  the person approves with their password through polkit
#                    instead of a root console, and their Core can ask for an
#                    approved decision to be staged
#   staging          one-time trial boots on GRUB or systemd-boot, confirmed or
#                    rolled back by the boot check
#
# Each is its own root service; the broker still decides alone, holds public
# keys only and has no network, and nothing here switches the running system.
{ config, lib, pkgs, ... }:
let
  cfg = config.services.nexus-guardian;
  builderDir = "/var/lib/nexus-guardian-builder";
  configFile = pkgs.writeText "nexus-guardian-config.json" (builtins.toJSON {
    stateDir = "/var/lib/nexus-guardian";
    socket = "/run/nexus-guardian/review.sock";
    socketGroup = "nexus-guardian";
    allowedUsers = cfg.allowedUsers;
    hostConfig = cfg.hostConfig;
    # With a local builder the keys file is written at boot: the configured
    # keys plus this computer's own builder key.
    trustedKeys = if cfg.localBuilder.enable then "${builderDir}/trusted-keys" else "/etc/nexus-guardian/trusted-keys";
  });
  requireUnits = lib.concatMapStrings (unit: " --require-unit " + lib.escapeShellArg unit) cfg.staging.requiredUnits;
  stageArgs = " --bootloader ${cfg.staging.bootloader} --esp ${lib.escapeShellArg config.boot.loader.efi.efiSysMountPoint}";
  approveHelper = "/run/current-system/sw/bin/nexus-guardian-desktop-approve";
  # auth_admin, not auth_admin_keep: every approval asks for the password again.
  polkitPolicy = pkgs.writeTextDir "share/polkit-1/actions/org.nexusos.guardian.policy" ''
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
     "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
    <policyconfig>
      <vendor>NexusOS</vendor>
      <action id="org.nexusos.guardian.approve">
        <description>Approve a system change</description>
        <message>Enter your password to approve this system change. It is used from the next restart, and the computer goes back to today's version if it does not start properly.</message>
        <icon_name>security-high</icon_name>
        <defaults>
          <allow_any>no</allow_any>
          <allow_inactive>no</allow_inactive>
          <allow_active>auth_admin</allow_active>
        </defaults>
        <annotate key="org.freedesktop.policykit.exec.path">${approveHelper}</annotate>
      </action>
    </policyconfig>
  '';
  # Root with no capabilities and no network, as for the broker.
  confined = {
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
  };
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
      default = [ ];
      description = "Base64 Ed25519 public keys of builders whose attestations are trusted (besides the local builder's).";
    };
    localBuilder = {
      enable = lib.mkEnableOption ''
        building, VM-testing and signing candidates on this computer. Its Ed25519
        key is made at first boot, readable by root only, and trusted by the broker'';
      vmTestSeconds = lib.mkOption {
        type = lib.types.ints.between 120 14400;
        default = 1800;
        description = "How long the candidate's test machine may take to start up.";
      };
    };
    desktopApproval.enable = lib.mkEnableOption ''
      approval from the desktop: the person enters their password through polkit
      (an administrator's, every time) instead of typing at a root console, and
      their Core may ask for their own approved decisions to be staged'';
    staging = {
      enable = lib.mkEnableOption ''
        one-time trial boots of broker-approved systems. Failed boots reset
        automatically and the bootloader returns to the previous generation; a
        post-boot check confirms healthy trials or restores the baseline'';
      bootloader = lib.mkOption {
        type = lib.types.enum [ "grub" "systemd-boot" ];
        default = if config.boot.loader.systemd-boot.enable then "systemd-boot" else "grub";
        defaultText = lib.literalExpression ''if config.boot.loader.systemd-boot.enable then "systemd-boot" else "grub"'';
        description = "Bootloader used for trial boots: GRUB's next_entry, or systemd-boot's one-shot EFI entry.";
      };
      requiredUnits = lib.mkOption {
        type = lib.types.listOf lib.types.str;
        default = [ ];
        description = "Units that must be active for a trial boot (and the local builder's test machine) to pass.";
      };
      panicRebootSeconds = lib.mkOption {
        type = lib.types.ints.between 1 300;
        default = 10;
        description = "During a trial boot, reboot this many seconds after a kernel panic, so a failed trial falls back unattended.";
      };
    };
  };

  config = lib.mkIf cfg.enable {
    assertions = [
      { assertion = cfg.trustedBuilderKeys != [ ] || cfg.localBuilder.enable;
        message = "services.nexus-guardian.trustedBuilderKeys must not be empty unless localBuilder is enabled"; }
      { assertion = !(lib.elem "root" cfg.allowedUsers); message = "root cannot be a Guardian broker client"; }
    ];
    users.groups.nexus-guardian = { };
    # Group membership only grants reaching the sockets; every service still
    # checks each caller's kernel-reported UID against allowedUsers.
    users.users = lib.genAttrs cfg.allowedUsers (_: { extraGroups = [ "nexus-guardian" ]; });
    environment.systemPackages = [ cfg.package ] ++ lib.optional cfg.desktopApproval.enable polkitPolicy;
    security.polkit.enable = lib.mkIf cfg.desktopApproval.enable true;
    environment.etc."nexus-guardian/config.json" = { source = configFile; mode = "0444"; };
    environment.etc."nexus-guardian/trusted-keys" = {
      text = lib.concatMapStrings (key: key + "\n") cfg.trustedBuilderKeys;
      mode = "0444";
    };

    boot.loader.grub.default = lib.mkIf (cfg.staging.enable && cfg.staging.bootloader == "grub") "saved";
    # The trial boot's own entry: the same system, but a stage-1 failure panics
    # and the panic resets, so a broken trial falls back unattended. Only the
    # one-time trial boots it (stage_executor.py, TRIAL_SPECIALISATION); every
    # other boot stops at a failure (a file system check that needs repair, say)
    # and offers the repair shell instead of rebooting in a loop.
    specialisation = lib.mkIf cfg.staging.enable {
      guardian-trial.configuration = {
        boot.kernelParams = [
          "panic=${toString cfg.staging.panicRebootSeconds}"
          "boot.panic_on_fail"
        ];
        boot.loader.grub.configurationName = "Guardian trial boot";
      };
    };
    systemd.services.nexus-guardian-boot-check = lib.mkIf cfg.staging.enable {
      description = "NexusOS Guardian trial-boot assessment";
      wantedBy = [ "multi-user.target" ];
      # After the desktop has started too, where there is one.
      after = [ "multi-user.target" "graphical.target" "nix-daemon.socket" ];
      serviceConfig = {
        Type = "oneshot";
        RemainAfterExit = true;
        ExecStart = "${cfg.package}/bin/nexus-guardian-boot-check" + requireUnits;
        StateDirectory = "nexus-guardian";
        StateDirectoryMode = "0700";
      };
    };

    # This computer's builder key: made once, never replaced, root only. Every
    # boot rewrites the broker's trusted keys (configured keys plus this one).
    systemd.services.nexus-guardian-keys = lib.mkIf cfg.localBuilder.enable {
      description = "NexusOS Guardian builder key and trusted keys";
      wantedBy = [ "multi-user.target" ];
      before = [ "nexus-guardian.service" "nexus-guardian-builder.service" ];
      serviceConfig = confined // {
        Type = "oneshot";
        RemainAfterExit = true;
        UMask = "0077";
        ExecStart = "${cfg.package}/bin/nexus-guardian-builder init-keys --dir ${builderDir} --static-keys /etc/nexus-guardian/trusted-keys";
        StateDirectory = "nexus-guardian-builder";
        StateDirectoryMode = "0700";
      };
    };

    # Builds through the Nix daemon (which fetches anything missing); the
    # builder itself has no network, and can write only its work directory.
    systemd.services.nexus-guardian-builder = lib.mkIf cfg.localBuilder.enable {
      description = "NexusOS Guardian local builder (builds, tests and signs; never activates)";
      wantedBy = [ "multi-user.target" ];
      requires = [ "nexus-guardian-keys.service" ];
      after = [ "nexus-guardian-keys.service" "nix-daemon.socket" ];
      path = [ config.nix.package ];
      environment = { NIX_REMOTE = "daemon"; HOME = "${builderDir}/work"; };
      serviceConfig = confined // {
        Type = "simple";
        User = "root";
        Group = "nexus-guardian";
        ExecStart = "${cfg.package}/bin/nexus-guardian-builder serve --config /etc/nexus-guardian/config.json --dir ${builderDir}"
          + " --vm-timeout ${toString cfg.localBuilder.vmTestSeconds}" + requireUnits;
        Restart = "on-failure";
        RestartSec = 5;
        UMask = "0077";
        RuntimeDirectory = "nexus-guardian-builder";
        RuntimeDirectoryMode = "0750";
        ReadWritePaths = [ "${builderDir}/work" ];
        # Evaluating a whole NixOS system needs room.
        MemoryMax = "4G";
        TasksMax = 64;
      };
    };

    # Stages only the caller's own permitted, unstaged decision (stage_service.py).
    # switch-to-configuration boot writes the bootloader, so this one is not confined.
    systemd.services.nexus-guardian-stage = lib.mkIf (cfg.desktopApproval.enable && cfg.staging.enable) {
      description = "NexusOS Guardian staging service (approved changes, next boot only)";
      wantedBy = [ "multi-user.target" ];
      requires = [ "nexus-guardian.service" ];
      after = [ "nexus-guardian.service" ];
      serviceConfig = {
        Type = "simple";
        User = "root";
        Group = "nexus-guardian";
        ExecStart = "${cfg.package}/bin/nexus-guardian-stage-service serve --config /etc/nexus-guardian/config.json" + stageArgs;
        Restart = "on-failure";
        RestartSec = 2;
        UMask = "0077";
        RuntimeDirectory = "nexus-guardian-stage";
        RuntimeDirectoryMode = "0750";
        NoNewPrivileges = true;
        PrivateNetwork = true;
        IPAddressDeny = "any";
      };
    };

    systemd.services.nexus-guardian = {
      description = "NexusOS Guardian review broker (decides; never activates)";
      wantedBy = [ "multi-user.target" ];
      requires = lib.optional cfg.localBuilder.enable "nexus-guardian-keys.service";
      after = [ "local-fs.target" "nix-daemon.socket" ] ++ lib.optional cfg.localBuilder.enable "nexus-guardian-keys.service";
      path = [ config.nix.package ];
      environment.NIX_REMOTE = "daemon";
      serviceConfig = confined // {
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
        ReadWritePaths = [ "/var/lib/nexus-guardian" "/run/nexus-guardian" ];
        LimitNOFILE = 64;
        TasksMax = 16;
        MemoryMax = "256M";
      };
    };
  };
}
