# Windows programs and games.
#  - Opening a .exe or .msi shows what it is and the app to use here
#    (WindowsProgram.qml, Core's list), with Bottles (Wine, from Flathub) for
#    anything else; nexus-windows-program is the handler for those file types.
#  - Game controllers: Steam's rules for Steam, Xbox, PlayStation and
#    Switch controllers (hardware.steam-hardware), Xbox controllers over
#    Bluetooth (xpadneo), and GameMode, which games ask for more speed.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.gamingWindows;
  types = [ "application/x-ms-dos-executable" "application/x-msdownload" "application/x-msi" "application/x-ms-installer"
            "application/vnd.microsoft.portable-executable" "application/x-msdos-program" ];
  handler = pkgs.writeShellScriptBin "nexus-windows-program" ''
    exec ${config.services.nexus-desktop.shellPackage}/bin/nexus-shell-ipc call windows open "''${1:-}"
  '';
  entry = pkgs.makeDesktopItem {
    name = "nexus-windows-program";
    desktopName = "Windows program";
    comment = "What a Windows program is, and the app to use on NexusOS";
    exec = "nexus-windows-program %f";
    icon = "application-x-ms-dos-executable";
    mimeTypes = types;
    noDisplay = true;
  };
in
{
  options.nexusos.gamingWindows.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Windows programs (what to use instead, Bottles) and game controllers.";
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ handler entry ];
    xdg.mime.defaultApplications = lib.genAttrs types (_: "nexus-windows-program.desktop");
    hardware.steam-hardware.enable = lib.mkDefault true;
    hardware.xpadneo.enable = lib.mkDefault true;
    programs.gamemode.enable = lib.mkDefault true;
  };
}
