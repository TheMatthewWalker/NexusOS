# A guest account (Settings, Accounts), off until turned on through Guardian
# (nexusos.guest.enable): "Guest" on the login screen, with no password, for
# someone borrowing the computer. Everything a guest does is wiped when they
# log out (and at every start, in case the computer was turned off with a
# guest logged in): their files, browser history, settings and sign-ins.
# A guest is not an administrator and cannot read other people's files
# (home folders are private), approve system changes or see the disk's key.
{ config, lib, pkgs, ... }:
let
  home = "/home/guest";
  uid = 1999;
  reset = pkgs.writeShellScript "nexus-guest-reset" ''
    # Nothing of the guest's may outlive the session: stop what is left, then start the folder afresh.
    ${pkgs.procps}/bin/pkill -KILL -u ${toString uid} || true
    ${pkgs.coreutils}/bin/rm -rf --one-file-system ${home}
    ${pkgs.coreutils}/bin/install -d -m 700 -o ${toString uid} -g users ${home}
    ${pkgs.coreutils}/bin/rm -rf /var/lib/systemd/linger/guest /var/tmp/guest-* 2>/dev/null || true
  '';
in
{
  options.nexusos.guest.enable = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "A guest account with no password on the login screen, wiped each time the guest logs out.";
  };

  config = lib.mkIf config.nexusos.guest.enable {
    users.users.guest = {
      isNormalUser = true;
      inherit uid home;
      description = "Guest";
      hashedPassword = "";
      createHome = true;
      extraGroups = [ "networkmanager" "video" "audio" ];
    };
    # Only the guest has no password (every other account needs 10 characters or more).
    security.pam.services.greetd.allowNullPassword = true;
    security.pam.services.hyprlock.allowNullPassword = true;
    environment.etc."nexus/greeter.env".text = lib.mkAfter "NEXUS_GREETER_GUEST=1\n";
    # When the guest's last session ends, their user manager stops: wipe then, as root ("+").
    environment.etc."systemd/system/user@${toString uid}.service.d/nexus-guest.conf".text = ''
      [Service]
      ExecStopPost=+${reset}
    '';
    systemd.services.nexus-guest-reset = {
      description = "Start the guest account afresh";
      wantedBy = [ "multi-user.target" ];
      before = [ "greetd.service" "display-manager.service" ];
      serviceConfig = { Type = "oneshot"; ExecStart = reset; };
    };
  };
}
