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
  } // cfg.extraEnvironment;
in
{
  options.services.nexus-core = {
    enable = lib.mkEnableOption "Nexus Core for every user session";
    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.callPackage ../packages/nexus-core.nix { };
      description = "Package providing nexus-core and the nexus CLI.";
    };
    ollamaModel = lib.mkOption {
      type = lib.types.str;
      default = "llama3.2:3b";
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
    systemd.user.services.nexus-core = {
      description = "Nexus Core (per-user AI daemon)";
      wantedBy = [ "default.target" ];
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
