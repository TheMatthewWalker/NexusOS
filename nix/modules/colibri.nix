# The very large model: colibri (nix/packages/colibri.nix, Apache-2.0) runs
# mixture-of-experts models bigger than the computer's memory by reading the
# experts from the disk as the model asks for them. Core starts its server on
# demand and Nexus answers with it once the person chooses it (Colibri.cs).
#
# This module only installs the engine and tells Core where it is: nothing is
# downloaded until the person turns it on in Settings, Privacy and AI. The
# download (19 GB to hundreds of GB) goes into their own folder,
# ~/.local/share/nexusos/colibri, as them, through nexus-colibri-model, which
# uses colibri's own resumable, checksummed downloader. No system change.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.colibri;
  fetch = pkgs.writeShellScriptBin "nexus-colibri-model" ''
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/colibri/nexus_colibri_model.py} --lib ${cfg.package}/lib/colibri "$@"
  '';
in
{
  options.nexusos.colibri = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = ''
        Offer the very large model. hardware.nix turns it on for x86-64
        computers with 8 GB of memory or more; Core still checks the processor
        (AVX2) before offering it.
      '';
    };
    model = lib.mkOption {
      type = lib.types.enum [ "qwen36-35b" "qwen3-coder-30b" "deepseek-v4-flash" "glm-5.2" ];
      default = "qwen36-35b";
      description = ''
        The model offered first (the person can choose another in Settings):
        Qwen3.6-35B-A3B (23 GB download, 10 GB of memory at least) answers at a
        few words a second on a laptop processor; DeepSeek V4 Flash (167 GB) and
        GLM-5.2 (429 GB) read most of themselves from the disk and are slow.
      '';
    };
    port = lib.mkOption { type = lib.types.port; default = 18436; description = "Loopback port for colibri's server."; };
    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.callPackage ../packages/colibri.nix { };
      description = "colibri: the engines, coli and its server.";
    };
  };

  config = lib.mkIf cfg.enable {
    services.nexus-core.extraEnvironment = {
      NEXUS_COLIBRI_BIN = "${cfg.package}/bin/coli";
      NEXUS_COLIBRI_FETCH = "${fetch}/bin/nexus-colibri-model";
      NEXUS_COLIBRI_MODEL = cfg.model;
      NEXUS_COLIBRI_PORT = toString cfg.port;
    };
    environment.systemPackages = [ cfg.package ];
    # colibri's licence and notices, with the other licences on the system.
    environment.etc."nexusos/licences/colibri".source = "${cfg.package}/share/doc/colibri";
  };
}
