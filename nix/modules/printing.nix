# Printing and scanning that work when the printer is switched on.
#  - CUPS with driverless printing: printers on the network (IPP Everywhere,
#    AirPrint, Mopria: nearly every printer from the last ten years) and USB
#    printers that speak IPP (ipp-usb) appear by themselves; Gutenprint covers
#    many older ones.
#  - Avahi finds them on the network (mDNS); its firewall port is opened.
#  - SANE with eSCL/WSD (sane-airscan) for network scanners and
#    all-in-ones, and Document Scanner (simple-scan) to use them.
#  - Settings, Printers lists them, sets the default and prints a test page;
#    system-config-printer is there for anything unusual.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.printing;
in
{
  options.nexusos.printing.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Printing and scanning, with printers and scanners found by themselves.";
  };

  config = lib.mkIf cfg.enable {
    services.printing = {
      enable = lib.mkDefault true;
      drivers = [ pkgs.gutenprint ];
    };
    services.ipp-usb.enable = lib.mkDefault true;
    services.avahi = {
      enable = lib.mkDefault true;
      nssmdns4 = lib.mkDefault true;
      openFirewall = lib.mkDefault true;
    };
    hardware.sane = {
      enable = lib.mkDefault true;
      extraBackends = [ pkgs.sane-airscan ];
    };
    users.users.${config.nexusos.user}.extraGroups = [ "scanner" "lp" ];
    environment.systemPackages = [ pkgs.simple-scan pkgs.system-config-printer ];
  };
}
