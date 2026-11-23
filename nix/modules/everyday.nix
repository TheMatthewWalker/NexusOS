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
#    Podman (no daemon running as root);
#  - archives in 7z and RAR, and one light or dark look for GTK and Qt apps.
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
  record = pkgs.writeShellApplication {
    name = "nexus-record";
    runtimeInputs = with pkgs; [ wf-recorder slurp jq libnotify xdg-user-dirs coreutils config.programs.hyprland.package ];
    text = builtins.readFile ../../scripts/desktop/nexus-record.sh;
  };
  startup = pkgs.writeShellScriptBin "nexus-startup" ''
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/desktop/nexus_startup.py} "$@"
  '';
  permissions = pkgs.writeShellScriptBin "nexus-permissions" ''
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/desktop/nexus_permissions.py} "$@"
  '';
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
                                   pkgs.cifs-utils pkgs.exfatprogs pkgs.ntfs3g pkgs.docker-compose
                                   pkgs.cliphist pkgs.wtype pkgs.wl-clipboard
                                   pkgs.networkmanagerapplet pkgs.zenity pkgs.wireguard-tools
                                   pkgs.p7zip pkgs.unar pkgs.adw-gtk3 pkgs.adwaita-qt pkgs.adwaita-qt6 pkgs.dconf
                                   record startup permissions pkgs.libva-utils pkgs.git-credential-manager ];

    # Startup apps (Settings, Default apps): the person's own autostart
    # entries, which Steam, Discord and Flatpak apps write when told to start
    # at login, are started with the session. /etc/xdg/autostart is left
    # alone: those are system parts NexusOS starts its own way.
    systemd.user.services.nexus-startup = {
      description = "Start the apps chosen to open at login";
      wantedBy = [ "nexus-session.target" ];
      partOf = [ "nexus-session.target" ];
      after = [ "nexus-session.target" "nexus-shell.service" ];
      environment.XDG_CURRENT_DESKTOP = "Hyprland";
      serviceConfig = { Type = "oneshot"; ExecStart = "${pkgs.dex}/bin/dex --autostart --search-paths %h/.config/autostart"; };
    };

    # Docks and Thunderbolt devices are authorised (bolt); videos decode on
    # the graphics chip (VA-API; Intel's driver here, AMD's is in Mesa), which
    # saves battery; security keys (FIDO2, U2F: YubiKey, passkeys) work in
    # browsers for the person at the screen.
    services.hardware.bolt.enable = lib.mkDefault true;
    hardware.graphics.extraPackages = lib.optionals pkgs.stdenv.hostPlatform.isx86_64 [ pkgs.intel-media-driver ];
    services.udev.packages = [ pkgs.libfido2 ];

    # git over HTTPS signs in to GitHub, GitLab or Azure DevOps in the browser
    # once (Git Credential Manager, as Git for Windows has) and keeps the
    # token in the keyring.
    programs.git.config.credential = { helper = "manager"; credentialStore = "secretservice"; };

    # Archives: File Roller opens 7z with p7zip and RAR with unar (free, no unrar licence).
    #
    # One look for every app: GTK 3 (adw-gtk3), GTK 4 and libadwaita, and Qt
    # (QGnomePlatform) all follow org.gnome.desktop.interface color-scheme,
    # dark to start with like the desktop itself. Settings, Desktop switches
    # it per person with dconf (DesktopSettings.qml, appTheme).
    programs.dconf.profiles.user.databases = [{
      settings."org/gnome/desktop/interface" = {
        color-scheme = "prefer-dark";
        gtk-theme = "adw-gtk3-dark";
      };
    }];
    qt = { enable = lib.mkDefault true; platformTheme = lib.mkDefault "gnome"; style = lib.mkDefault "adwaita-dark"; };

    # Networks: VPNs from a file or set up in the connection editor (Settings,
    # Network): OpenVPN, OpenConnect (Cisco AnyConnect, Palo Alto GlobalProtect,
    # Fortinet, Juniper) and L2TP; WireGuard is built in. Work and school
    # Wi-Fi (802.1X, eduroam) in the same editor. Phones through KDE Connect
    # (it opens its own ports, 1714-1764, on the local network).
    # (vpnc is left out: nixpkgs marks it insecure; OpenConnect covers Cisco.)
    networking.networkmanager.plugins = with pkgs; [ networkmanager-openvpn networkmanager-openconnect networkmanager-l2tp ];
    programs.kdeconnect.enable = lib.mkDefault true;

    # Clipboard history (Clipboard.qml): text and pictures, kept by cliphist in
    # the person's cache, for the desktop session only.
    systemd.user.services.nexus-clipboard = {
      description = "Clipboard history";
      partOf = [ "nexus-session.target" ];
      wantedBy = [ "nexus-session.target" ];
      after = [ "nexus-session.target" ];
      path = [ pkgs.wl-clipboard pkgs.cliphist ];
      script = ''
        wl-paste --type text --watch cliphist store &
        exec wl-paste --type image --watch cliphist store
      '';
      serviceConfig = { Restart = "on-failure"; RestartSec = 2; };
    };
    environment.etc."nexus/emoji-test.txt".source = "${pkgs.unicode-emoji}/share/unicode/emoji/emoji-test.txt";

    fonts.packages = with pkgs; [
      carlito caladea liberation_ttf dejavu_fonts
      noto-fonts noto-fonts-color-emoji noto-fonts-cjk-sans
    ];
    fonts.fontconfig.defaultFonts.emoji = [ "Noto Color Emoji" ];
  };
}
