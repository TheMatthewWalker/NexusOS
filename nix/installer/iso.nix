# The NexusOS installer image: a live NexusOS desktop that opens the
# graphical installer (shell/qml/Installer.qml); the terminal installer is a
# button away. Build it with `nix build .#iso`.
{ config, lib, pkgs, modulesPath, nexusSource, referenceSystem, referenceSystemNvidia, nixpkgsSource, bundledModel ? null,
  referenceInitrdInputs ? [ ], ... }:
let
  nexus-install = pkgs.callPackage ../packages/nexus-install.nix { };
  # What the graphical installer runs through pkexec, which starts programs
  # with a bare PATH: nixos-install and its helpers expect the system's.
  nexus-install-as-root = pkgs.writeShellScript "nexus-install-as-root" ''
    export PATH=/run/wrappers/bin:/run/current-system/sw/bin:$PATH
    exec ${nexus-install}/bin/nexus-install "$@"
  '';
  welcome = pkgs.writeShellScriptBin "nexus-install-welcome" ''
    cat <<'EOF'

      Welcome to NexusOS.

      Nothing is installed yet. Press Enter to install NexusOS on this
      computer, or close this window to go back.

      No internet connection is needed: everything is on this USB stick.

    EOF
    read -r _
    sudo ${nexus-install}/bin/nexus-install
    echo
    echo "Press Enter to close this window."
    read -r _
  '';
in
{
  imports = [
    "${modulesPath}/installer/cd-dvd/installation-cd-minimal.nix"
    ../modules/desktop.nix
    ../modules/nvidia.nix
  ];

  system.nixos.distroName = "NexusOS";
  system.nixos.distroId = "nexusos";
  image.baseName = lib.mkForce "nexusos-${config.system.nixos.release}-live-${pkgs.stdenv.hostPlatform.system}";
  isoImage.volumeID = lib.mkForce "NEXUSOS";
  # installation-cd-minimal turns udisks off; the desktop (Move in, Disks) uses it.
  services.udisks2.enable = lib.mkForce true;
  isoImage.appendToMenuLabel = " live";

  # Everything the installer needs is on the image: the installed system's
  # closure, the pinned nixpkgs source, and the tools that build the parts
  # specific to this computer's hardware. The NVIDIA variant carries the
  # driver, which the NixOS cache does not build.
  isoImage.storeContents = [ referenceSystem referenceSystemNvidia nixpkgsSource ]
    # The tools that build those parts, so installing works with no internet.
    ++ import ./offline-deps.nix pkgs
    # And what the initrd is made from (flake.nix, referenceInitrdInputs).
    ++ referenceInitrdInputs
    # The local model, when pinned (nix/models/); nexus-install copies it to the disk.
    ++ lib.optional (bundledModel != null) bundledModel;
  environment.etc."nexusos-model" = lib.mkIf (bundledModel != null) { source = bundledModel; };

  # A second boot entry, "NexusOS live (NVIDIA)", for machines whose screen
  # is driven by an NVIDIA card. The default entry uses the built-in open
  # driver, which is enough to install on most machines, laptops included.
  specialisation.nvidia.configuration = {
    nexusos.nvidia.enable = true;
    isoImage.configurationName = lib.mkForce "(NVIDIA)";
  };
  environment.etc."nexusos".source = nexusSource;

  # The live session offers only Wi-Fi and Install (shell/qml/live.qml):
  # Nexus, apps, the Welcome setup and system changes belong to the installed
  # system, so Core does not run here at all.
  services.nexus-desktop = {
    enable = true;
    live = true;
    autologinUser = "nixos";
    coreDetail = 40;
    extraHyprlandConfig = "env = NEXUS_INSTALL,${nexus-install-as-root}";
  };
  # The graphical installer runs exactly that program as root through pkexec,
  # and the live user may do so without a password: the live system keeps
  # nothing, and its user already has sudo without one.
  security.polkit.enable = true;
  security.polkit.extraConfig = ''
    polkit.addRule(function (action, subject) {
      if (action.id == "org.freedesktop.policykit.exec" && subject.user == "nixos" &&
          action.lookup("program") == "${nexus-install-as-root}") {
        return polkit.Result.YES;
      }
    });
  '';
  services.nexus-core.enable = false;
  # Wi-Fi: NetworkManager runs wpa_supplicant under its own control
  # (networking.wireless.enable with dbusControlled). Turning
  # networking.wireless off here once left the live system with no Wi-Fi at all.
  # NexusOS installs to ext4; leave ZFS out of the image.
  boot.supportedFilesystems.zfs = lib.mkForce false;
  hardware.enableRedistributableFirmware = true;
  hardware.graphics.enable = true;

  nix.settings.experimental-features = [ "nix-command" "flakes" ];
  environment.systemPackages = [ nexus-install welcome pkgs.git ];
}
