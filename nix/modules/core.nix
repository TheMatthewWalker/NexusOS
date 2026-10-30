# NixOS module for Nexus Core: one unprivileged instance per logged-in user,
# reachable only through that user's peer-authenticated Unix socket.
{ config, lib, pkgs, ... }:
let
  cfg = config.services.nexus-core;
  environment = {
    NEXUS_API_MODE = "unix";
    NEXUS_OLLAMA_MODEL = cfg.ollamaModel;
    NEXUS_CLOUD_EGRESS_ALLOWED = lib.boolToString cfg.allowCloudEgress;
    NEXUS_GUARDIAN_SOCKET = "/run/nexus-guardian/review.sock";
    # The coding agent runs a project's tests only inside bubblewrap, never without it.
    NEXUS_BWRAP = "${pkgs.bubblewrap}/bin/bwrap";
  } // cfg.extraEnvironment;
in
{
  # `nexus try` needs nexus-try; services.nexus-desktop.tryApps.enable turns it off.
  imports = [ ./try-apps.nix ];

  options.services.nexus-core = {
    enable = lib.mkEnableOption "Nexus Core for every user session";
    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.callPackage ../packages/nexus-core.nix { };
      description = "Package providing nexus-core and the nexus CLI.";
    };
    ollamaModel = lib.mkOption {
      type = lib.types.str;
      default = "qwen3:8b";
      description = "Local Ollama model used by the default provider.";
    };
    allowCloudEgress = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Allow prompts to be sent to the configured cloud provider. Off by default.";
    };
    extraEnvironment = lib.mkOption {
      type = lib.types.attrsOf lib.types.str;
      default = { };
      example = { NEXUS_HISTORY_MAX_DAYS = "7"; };
      description = "Additional NEXUS_* settings passed to every user's Core.";
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package ];
    # bubblewrap (the coding agent's test sandbox) runs unprivileged, in a user namespace.
    security.allowUserNamespaces = lib.mkDefault true;
    systemd.user.services.nexus-core = {
      description = "Nexus Core (per-user AI daemon)";
      wantedBy = [ "default.target" ];
      # Only for people: system accounts such as the login screen's greeter
      # also get a user manager, and have no writable home for Core's state.
      unitConfig.ConditionUser = "!@system";
      inherit environment;
      serviceConfig = {
        ExecStart = "${cfg.package}/bin/nexus-core";
        Restart = "on-failure";
        RestartSec = 2;
        NoNewPrivileges = true;
        UMask = "0077";
      };
    };
  };
}
