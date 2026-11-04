# Software on a NexusOS machine:
#  - everyday apps installed from the start (nexusos.apps.preinstalled);
#  - Flatpak with Flathub, which the Apps window installs from per person,
#    with no password and no system change;
#  - `apt`, which runs apt in an Ubuntu container (distrobox) for software
#    that is only packaged for Ubuntu or Debian. That container is not a
#    sandbox: Ubuntu software runs as the person and can read their home
#    folder. `apt --isolated` uses a second container (plain podman) with
#    its own home folder, for software they do not fully trust.
# Installs and removals from Apps and from apt are journalled by Nexus Core
# with an Undo (src/NexusOS.Core/AppChanges.cs).
# Software for the whole system is still added in /etc/nixos/local.nix and
# applied through Guardian.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.apps;
  apt = pkgs.writeShellApplication {
    name = "apt";
    runtimeInputs = with pkgs; [ distrobox podman coreutils util-linux gawk ];
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
      description = ''
        The `apt` command: apt in an Ubuntu container (distrobox and podman).
        The container shares the home folder and is not a sandbox;
        `apt --isolated` uses a separate one that cannot see it.
      '';
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
        terminal = "kitty"; files = "thunar"; browser = "firefox";
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
      # Security updates for Flathub apps, every day once online: the
      # person's own apps (Apps installs with --user) and any for everyone.
      # An app that is running keeps its old version until it is restarted.
      systemd.services.nexus-flatpak-update = {
        description = "Update Flathub apps installed for everyone";
        after = [ "network-online.target" "nexus-flathub.service" ];
        wants = [ "network-online.target" ];
        path = [ pkgs.flatpak ];
        serviceConfig = { Type = "oneshot"; Nice = 10; IOSchedulingClass = "idle"; };
        script = "flatpak update --system --noninteractive -y";
      };
      systemd.timers.nexus-flatpak-update = {
        wantedBy = [ "timers.target" ];
        timerConfig = { OnBootSec = "15min"; OnUnitActiveSec = "1d"; Persistent = true; RandomizedDelaySec = "1h"; };
      };
      systemd.user.services.nexus-flatpak-update = {
        description = "Update your Flathub apps";
        path = [ pkgs.flatpak ];
        serviceConfig = { Type = "oneshot"; Nice = 10; IOSchedulingClass = "idle"; };
        script = "flatpak update --user --noninteractive -y";
      };
      systemd.user.timers.nexus-flatpak-update = {
        wantedBy = [ "timers.target" ];
        timerConfig = { OnStartupSec = "20min"; OnUnitActiveSec = "1d"; Persistent = true; RandomizedDelaySec = "1h"; };
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
