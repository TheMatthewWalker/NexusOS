# The NexusOS installer image: a live NexusOS desktop that opens the
# installer in a terminal. Build it with `nix build .#iso`.
{ config, lib, pkgs, modulesPath, nexusSource, referenceSystem, referenceSystemNvidia, nixpkgsSource, ... }:
let
  nexus-install = pkgs.callPackage ../packages/nexus-install.nix { };
  welcome = pkgs.writeShellScriptBin "nexus-install-welcome" ''
    cat <<'EOF'

      Welcome to NexusOS.

      You are running the live system: nothing is installed yet, and nothing
      you do here is kept. Close this window to keep exploring.

      To install NexusOS on this computer, connect to the internet
      (the system panel, top right), then press Enter.

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

  # Most of what the installer needs is already on the image: the installed
  # system's closure and the pinned nixpkgs source, so installing downloads
  # little more than the parts built for this computer's hardware. The NVIDIA
  # variant carries the driver, which the NixOS cache does not build.
  isoImage.storeContents = [ referenceSystem referenceSystemNvidia nixpkgsSource ];

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
  # The desktop uses NetworkManager; the minimal image's wpa_supplicant would clash.
  networking.wireless.enable = lib.mkForce false;
  # NexusOS installs to ext4; leave ZFS out of the image.
  boot.supportedFilesystems.zfs = lib.mkForce false;
  hardware.enableRedistributableFirmware = true;
  hardware.graphics.enable = true;

  nix.settings.experimental-features = [ "nix-command" "flakes" ];
  environment.systemPackages = [ nexus-install welcome pkgs.git ];
}
