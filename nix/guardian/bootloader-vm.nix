{ lib, config, pkgs, ... }:
{
  system.stateVersion = "26.05";
  # The standalone NixOS evaluation needs a root declaration; the bootloader
  # VM variant creates and mounts its own root disk image.
  fileSystems."/" = lib.mkIf (!config.virtualisation.useBootLoader) {
    device = "/dev/vda1";
    fsType = "ext4";
  };
  networking.hostName = "nexus-recovery-lab";
  boot.loader.grub.enable = true;
  boot.loader.grub.device = "/dev/vda";
  boot.loader.timeout = 3;
  boot.kernelParams = [ "console=ttyS0,115200n8" ];
  services.openssh.enable = true;
  services.openssh.settings.PasswordAuthentication = false;
  services.openssh.settings.PermitRootLogin = "no";
  systemd.services.nexus-generation-identity = {
    wantedBy = [ "multi-user.target" ];
    serviceConfig.Type = "oneshot";
    script = "echo known-good > /run/nexus-generation-identity";
  };
  virtualisation.vmVariantWithBootLoader = {
    virtualisation = {
      memorySize = 1536;
      cores = 2;
      graphics = false;
      diskSize = 4096;
    };
  };
}
