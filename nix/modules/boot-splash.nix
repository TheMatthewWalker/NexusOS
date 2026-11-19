# The start-up screen: Plymouth with the NexusOS theme (nix/packages/
# nexus-plymouth.nix) from the boot menu to the login screen, including the
# prompt that unlocks an encrypted disk (with the layout from keyboard.nix).
# Kernel and systemd messages are kept off the screen; Esc shows them, and
# they are all in the journal (journalctl -b).
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.bootSplash;
in
{
  options.nexusos.bootSplash.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Show the NexusOS start-up screen instead of boot messages.";
  };

  config = lib.mkIf cfg.enable {
    boot.plymouth = {
      enable = true;
      theme = "nexusos";
      themePackages = [ (pkgs.callPackage ../packages/nexus-plymouth.nix { }) ];
    };
    boot.consoleLogLevel = lib.mkDefault 3;
    boot.initrd.verbose = lib.mkDefault false;
    boot.kernelParams = [ "quiet" "splash" "udev.log_level=3" "rd.udev.log_level=3" ];
  };
}
