# Software on a NexusOS machine:
#  - everyday apps installed from the start (nexusos.apps.preinstalled);
#  - Flatpak with Flathub, which the Apps window installs from per person,
#    with no password and no system change;
#  - `apt`, which runs apt in an Ubuntu container (distrobox) for software
#    that is only packaged for Ubuntu or Debian.
# Software for the whole system is still added in /etc/nixos/local.nix and
# applied through Guardian.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.apps;
  apt = pkgs.writeShellApplication {
    name = "apt";
    runtimeInputs = with pkgs; [ distrobox podman coreutils util-linux ];
    text = builtins.readFile ../packages/nexus-apt.sh;
  };
in
{
  options.nexusos.apps = {
    preinstalled = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Everyday apps: Firefox and Chromium, LibreOffice, VLC, an image
        viewer, a text editor, VSCodium, a calculator, a PDF reader and an
        archive manager.
      '';
    };
    flatpak = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Flatpak with Flathub, for the Apps window.";
    };
    apt = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "The `apt` command: apt in an Ubuntu container (distrobox and podman).";
    };
  };

  config = lib.mkMerge [
    (lib.mkIf cfg.preinstalled {
      programs.firefox.enable = true;
      environment.systemPackages = with pkgs; [
        chromium
        libreoffice-fresh
        vlc
        loupe                 # images
        gnome-text-editor
        vscodium
        gnome-calculator
        papers                # PDF
        file-roller           # archives
        gnome-disk-utility
      ];
      # Thumbnails and mounted drives in the file manager.
      services.gvfs.enable = lib.mkDefault true;
      services.tumbler.enable = lib.mkDefault true;
      services.nexus-desktop.apps = lib.mkDefault {
        terminal = "kitty"; files = "thunar"; browser = "firefox"; studio = "codium";
      };
    })

    (lib.mkIf cfg.flatpak {
      services.flatpak.enable = true;
      # Flathub is added once the machine is online; until then the Apps
      # window says it is not available yet.
      systemd.services.nexus-flathub = {
        description = "Add the Flathub app store";
        wantedBy = [ "multi-user.target" ];
        after = [ "network-online.target" ];
        wants = [ "network-online.target" ];
        path = [ pkgs.flatpak ];
        serviceConfig = { Type = "oneshot"; Restart = "on-failure"; RestartSec = 120; };
        script = "flatpak remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo";
      };
      # Flatpak apps find their portals (file pickers, screenshots).
      xdg.portal = {
        enable = true;
        extraPortals = [ pkgs.xdg-desktop-portal-gtk ];
        config.common.default = [ "hyprland" "gtk" ];
      };
      # Flatpak apps appear in the launcher.
      environment.sessionVariables.XDG_DATA_DIRS = [ "/var/lib/flatpak/exports/share" "$HOME/.local/share/flatpak/exports/share" ];
    })

    (lib.mkIf cfg.apt {
      virtualisation.podman.enable = true;
      environment.systemPackages = [ apt pkgs.distrobox ];
    })
  ];
}
