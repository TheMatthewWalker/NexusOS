# The NexusOS installer image: a live NexusOS desktop that opens the
# installer in a terminal. Build it with `nix build .#iso`.
{ config, lib, pkgs, modulesPath, nexusSource, referenceSystem, referenceSystemNvidia, nixpkgsSource, bundledModel ? null, ... }:
let
  nexus-install = pkgs.callPackage ../packages/nexus-install.nix { };
  welcome = pkgs.writeShellScriptBin "nexus-install-welcome" ''
    cat <<'EOF'

      Welcome to NexusOS.

      You are running the live system: nothing is installed yet, and nothing
      you do here is kept. Close this window to keep exploring.

      To install NexusOS on this computer, press Enter. No internet
      connection is needed: everything is on this USB stick. To connect
      anyway, open the system panel (top right) and pick a Wi-Fi network.

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
  isoImage.appendToMenuLabel = " live";

  # Everything the installer needs is on the image: the installed system's
  # closure, the pinned nixpkgs source, and the tools that build the parts
  # specific to this computer's hardware. The NVIDIA variant carries the
  # driver, which the NixOS cache does not build.
  isoImage.storeContents = [ referenceSystem referenceSystemNvidia nixpkgsSource ]
    # The tools that build those parts, so installing works with no internet.
    ++ import ./offline-deps.nix pkgs
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

  services.nexus-desktop = {
    enable = true;
    autologinUser = "nixos";
    coreDetail = 40;
    extraHyprlandConfig = ''
      exec-once = kitty --title "Install NexusOS" ${welcome}/bin/nexus-install-welcome
    '';
  };
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
