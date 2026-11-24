# Broadcom's own Wi-Fi driver ("wl", broadcom-sta), for the Broadcom chips
# the kernel has no driver for (BCM4352, BCM4360 and others in older
# MacBooks and some laptops). Off until turned on through Guardian
# (nexusos.hardware.broadcomWifi.enable), which marks it elevated: the
# driver is closed, no longer maintained, and has known flaws that a nearby
# attacker could use (CVE-2019-9501, CVE-2019-9502). Settings, Network offers
# it only when such a chip has no driver, and says that a supported USB or
# M.2 Wi-Fi card is the safer fix.
{ config, lib, ... }:
let
  wl = config.boot.kernelPackages.broadcom_sta;
in
{
  options.nexusos.hardware.broadcomWifi.enable = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "Broadcom's own Wi-Fi driver (wl) for chips the kernel cannot drive. Closed and unmaintained.";
  };

  config = lib.mkIf config.nexusos.hardware.broadcomWifi.enable {
    boot.extraModulePackages = [ wl ];
    boot.kernelModules = [ "wl" ];
    # The open drivers would claim the chip first and fail.
    boot.blacklistedKernelModules = [ "b43" "bcma" "ssb" "brcmsmac" "brcmfmac" ];
    nexusos.unfree.names = [ "broadcom-sta" ];
    nixpkgs.config.permittedInsecurePackages = [ wl.name ];
  };
}
