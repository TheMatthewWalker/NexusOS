# Everyday basics that a Windows computer has out of the box:
#  - Bluetooth on (mice, keyboards, headphones), with good audio codecs
#    (PipeWire and WirePlumber pick AAC, aptX or LDAC when the device has them);
#  - a password keyring (gnome-keyring) unlocked at login with the login
#    password, so browsers, mail, Wi-Fi and Nexus's cloud keys are kept safely
#    and asked for once; "Passwords and Keys" (Seahorse) shows them. When the
#    password changes in Settings, Accounts, nexus-keyring-password changes the
#    keyring's with it;
#  - fonts with the same sizes as Microsoft's (Carlito for Calibri, Caladea for
#    Cambria, Liberation for Arial, Times New Roman and Courier New), so Office
#    documents keep their layout, plus emoji and Chinese, Japanese and Korean;
#  - screenshots (nexus-screenshot) and Windows' Super+arrow snapping
#    (nexus-snap), bound in desktop.nix;
#  - memory that does not run out quietly: compressed swap in memory (zram)
#    and systemd-oomd ending the biggest offender before the computer freezes;
#  - programs downloaded from the internet run (nix-ld gives them the usual
#    Linux libraries: VS Code extensions, language servers, pip wheels, game
#    tools), AppImages run by double-click, and USB drives in NTFS or exFAT,
#    and Windows shares (SMB), work;
#  - for developers: git, and the `docker` and `docker compose` commands on
#    Podman (no daemon running as root).
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.everyday;
  python = pkgs.python3.withPackages (ps: [ ps.dbus-next ]);
  keyringPassword = pkgs.writeShellScriptBin "nexus-keyring-password" ''
    exec ${python}/bin/python3 -I ${../../scripts/desktop/nexus_keyring.py}
  '';
  screenshot = pkgs.writeShellApplication {
    name = "nexus-screenshot";
    runtimeInputs = with pkgs; [ grim slurp jq wl-clipboard libnotify xdg-user-dirs coreutils config.programs.hyprland.package ];
    text = builtins.readFile ../../scripts/desktop/nexus-screenshot.sh;
  };
  snap = pkgs.writeShellApplication {
    name = "nexus-snap";
    runtimeInputs = with pkgs; [ jq gawk config.programs.hyprland.package ];
    text = builtins.readFile ../../scripts/desktop/nexus-snap.sh;
  };
in
{
  options.nexusos.everyday.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Bluetooth, a password keyring, Microsoft-compatible fonts, screenshots and window snapping.";
  };

  config = lib.mkIf cfg.enable {
    hardware.bluetooth = {
      enable = lib.mkDefault true;
      powerOnBoot = lib.mkDefault true;
      # Battery levels of headphones and mice.
      settings.General.Experimental = lib.mkDefault true;
    };

    services.gnome.gnome-keyring.enable = lib.mkDefault true;
    security.pam.services.greetd.enableGnomeKeyring = lib.mkDefault true;
    security.pam.services.login.enableGnomeKeyring = lib.mkDefault true;
    programs.seahorse.enable = lib.mkDefault true;

    zramSwap.enable = lib.mkDefault true;
    systemd.oomd = { enable = lib.mkDefault true; enableUserSlices = lib.mkDefault true; enableRootSlice = lib.mkDefault true; };

    programs.nix-ld.enable = lib.mkDefault true;
    programs.appimage = { enable = lib.mkDefault true; binfmt = lib.mkDefault true; };
    boot.supportedFilesystems = { ntfs = lib.mkDefault true; exfat = lib.mkDefault true; };
    programs.git.enable = lib.mkDefault true;
    virtualisation.podman.dockerCompat = lib.mkDefault true;

    environment.systemPackages = [ pkgs.libsecret keyringPassword screenshot snap pkgs.playerctl
                                   pkgs.cifs-utils pkgs.exfatprogs pkgs.ntfs3g pkgs.docker-compose ];

    fonts.packages = with pkgs; [
      carlito caladea liberation_ttf dejavu_fonts
      noto-fonts noto-fonts-color-emoji noto-fonts-cjk-sans
    ];
    fonts.fontconfig.defaultFonts.emoji = [ "Noto Color Emoji" ];
  };
}
