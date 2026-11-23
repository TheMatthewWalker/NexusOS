# Sharing folders with other computers on this network (Settings, Network),
# and what the firewall lets in (/etc/nexus/firewall.json, shown there too),
# as Windows' Share tab. Off until turned on through Guardian
# (nexusos.sharing.enable): it opens Samba's ports (and WS-Discovery's,
# so this computer shows in Windows' Network) on the local firewall. Each
# person then shares their own folders (nexus-share, Samba usershares), with
# no password and no further system change. Shares are read-only to others.
{ config, lib, pkgs, ... }:
let
  share = pkgs.writeShellScriptBin "nexus-share" ''
    export PATH=${lib.makeBinPath [ config.services.samba.package ]}:$PATH
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/desktop/nexus_share.py} "$@"
  '';
in
{
  options.nexusos.sharing.enable = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "Share folders with other computers on this network (Samba, read-only, through Settings).";
  };

  config = lib.mkMerge [
    # The helper is always there, so Settings can say sharing is off.
    # The firewall's openings, for Settings, Network to say in words (it cannot read the rules).
    { environment.systemPackages = [ share ];
      environment.etc."nexus/firewall.json".text = builtins.toJSON {
        inherit (config.networking.firewall) enable allowedTCPPorts allowedUDPPorts allowedTCPPortRanges allowedUDPPortRanges;
      };
    }
    (lib.mkIf config.nexusos.sharing.enable {
      services.samba = {
        enable = true;
        openFirewall = true;
        usershares.enable = true;
        settings.global = {
          "server string" = "NexusOS";
          "server min protocol" = "SMB2_10";
          "map to guest" = "Bad User";
          "usershare allow guests" = "yes";
          "usershare owner only" = "yes";
          "usershare max shares" = "20";
        };
      };
      services.samba-wsdd = { enable = true; openFirewall = true; };
      users.users.${config.nexusos.user}.extraGroups = [ config.services.samba.usershares.group ];
    })
  ];
}
