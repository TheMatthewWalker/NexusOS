# Settings, Accounts: change your password (and the disk's with it), and add
# or remove accounts for other people. The work is done by
# scripts/accounts/nexus_account.py as root, through pkexec; polkit decides
# who may:
#   nexus-account-password  your own password: asks for your current password
#                           (the helper only ever changes the caller's)
#   nexus-account-manage    add or remove an account: asks for an administrator's
# Accounts are ordinary Linux accounts (users.mutableUsers, NixOS's default);
# the first account, made by the installer, is declared in nexusos.nix and
# cannot be removed here.
{ config, lib, pkgs, ... }:
let
  firstAccount = config.nexusos.user;
  tools = lib.makeBinPath [ pkgs.cryptsetup pkgs.shadow pkgs.systemd pkgs.coreutils ];
  # One command per permission, each running only its own operations.
  helper = name: ops: pkgs.writeShellScript name ''
    case "''${1:-}" in
      ${lib.concatStringsSep "|" ops}) ;;
      *) echo '{"error": "Not an operation of ${name}"}'; exit 2 ;;
    esac
    export PATH=${tools}
    export NEXUS_FIRST_ACCOUNT=${lib.escapeShellArg firstAccount}
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/accounts/nexus_account.py} "$1"
  '';
  passwordHelper = helper "nexus-account-password" [ "password" "disk" ];
  manageHelper = helper "nexus-account-manage" [ "add" "remove" ];
  commands = pkgs.runCommand "nexus-account" { } ''
    mkdir -p $out/bin
    ln -s ${passwordHelper} $out/bin/nexus-account-password
    ln -s ${manageHelper} $out/bin/nexus-account-manage
    cat > $out/bin/nexus-account <<EOF
    #!${pkgs.runtimeShell}
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/accounts/nexus_account.py} list
    EOF
    chmod +x $out/bin/nexus-account
  '';
  # pkexec matches the path it is given, as for Guardian's approval helper: the
  # shell runs these from /run/current-system/sw/bin, so that is the path here.
  policy = pkgs.writeTextDir "share/polkit-1/actions/org.nexusos.accounts.policy" ''
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
     "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
    <policyconfig>
      <vendor>NexusOS</vendor>
      <action id="org.nexusos.accounts.password">
        <description>Change your password</description>
        <message>Enter your current password to change it (it also unlocks the disk at start-up).</message>
        <defaults>
          <allow_any>no</allow_any>
          <allow_inactive>no</allow_inactive>
          <allow_active>auth_self</allow_active>
        </defaults>
        <annotate key="org.freedesktop.policykit.exec.path">/run/current-system/sw/bin/nexus-account-password</annotate>
      </action>
      <action id="org.nexusos.accounts.manage">
        <description>Add or remove an account</description>
        <message>An administrator's password is needed to add or remove an account on this computer.</message>
        <defaults>
          <allow_any>no</allow_any>
          <allow_inactive>no</allow_inactive>
          <allow_active>auth_admin</allow_active>
        </defaults>
        <annotate key="org.freedesktop.policykit.exec.path">/run/current-system/sw/bin/nexus-account-manage</annotate>
      </action>
    </policyconfig>
  '';
in
{
  options.nexusos.accounts.enable = lib.mkOption {
    type = lib.types.bool;
    default = true;
    description = "Settings, Accounts: change your password and the disk's with it; add and remove accounts.";
  };

  config = lib.mkIf config.nexusos.accounts.enable {
    security.polkit.enable = true;
    environment.systemPackages = [ commands policy ];
  };
}
