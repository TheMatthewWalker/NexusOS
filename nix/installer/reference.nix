# A stand-in for the hardware-configuration.nix and local.nix that
# nexus-install writes. It is built into the installer image so that an
# install finds almost everything it needs already on the USB stick, and CI
# evaluates and builds it to keep the installed system working.
{ lib, ... }:
{
  nexusos.user = "nexus";
  boot.initrd.availableKernelModules = [ "xhci_pci" "ahci" "nvme" "usb_storage" "usbhid" "sd_mod" "virtio_pci" "virtio_blk" "virtio_scsi" ];
  boot.kernelModules = [ "kvm-intel" "kvm-amd" ];
  fileSystems."/" = { device = "/dev/disk/by-label/nexusos"; fsType = "ext4"; };
  fileSystems."/boot" = { device = "/dev/disk/by-label/BOOT"; fsType = "vfat"; options = [ "umask=0077" ]; };
  nixpkgs.hostPlatform = lib.mkDefault "x86_64-linux";
}
