# "Try before you install": puts nexus-try and bubblewrap on the system so any
# user can run a nixpkgs app in a throwaway sandbox. Keeping an app goes
# through Guardian (`nexus try keep <attr>`); this module installs nothing else.
{ config, lib, pkgs, ... }:
let
  cfg = config.services.nexus-desktop.tryApps;
in
{
  options.services.nexus-desktop.tryApps = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Provide nexus-try, which runs nixpkgs apps in a throwaway bubblewrap sandbox without installing them.";
    };
    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.callPackage ../packages/nexus-try.nix { };
      description = "Package providing nexus-try.";
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package pkgs.bubblewrap ];
    # bubblewrap runs unprivileged, in a user namespace.
    security.allowUserNamespaces = lib.mkDefault true;
  };
}
