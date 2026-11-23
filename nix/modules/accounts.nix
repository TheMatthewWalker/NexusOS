# Settings, Accounts: change your password (and the disk's with it), and add
# or remove accounts for other people. The work is done by
# scripts/accounts/nexus_account.py as root, through pkexec; polkit decides
# who may:
#   nexus-account-password  your own password: asks for your current password
#                           (the helper only ever changes the caller's)
#   nexus-account-manage    add or remove an account, a child's screen time, the
#                           TPM unlocking the disk: asks for an administrator's
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
    export NEXUS_SCREEN_TIME=${screenTime}/bin/nexus-screen-time
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/accounts/nexus_account.py} "$1"
  '';
  passwordHelper = helper "nexus-account-password" [ "password" "disk" ];
  # The TPM unlocking the disk is for the whole computer, so an administrator approves it too.
  manageHelper = helper "nexus-account-manage" [ "add" "remove" "limits" "tpm" ];
  # Screen time for children's accounts (Settings, Accounts): checked at login
  # (PAM), counted each minute, and the child signed out when it is over.
  screenTime = pkgs.writeShellScriptBin "nexus-screen-time" ''
    export PATH=${lib.makeBinPath [ pkgs.systemd pkgs.util-linux pkgs.libnotify pkgs.coreutils ]}
    exec ${pkgs.python3}/bin/python3 -I ${../../scripts/accounts/nexus_screen_time.py} "$@"
  '';
  screenTimeRule = {
    order = config.security.pam.services.login.rules.account.unix.order - 10;
    control = "requisite";
    modulePath = "${pkgs.linux-pam}/lib/security/pam_exec.so";
    args = [ "quiet" "stdout" "${screenTime}/bin/nexus-screen-time" "check" ];
  };
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
        <description>Change the accounts on this computer</description>
        <message>An administrator's password is needed to add or remove an account, set screen time, or change how the disk unlocks.</message>
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
    environment.systemPackages = [ commands policy screenTime ];
    security.pam.services.greetd.rules.account.nexus-screen-time = screenTimeRule;
    security.pam.services.login.rules.account.nexus-screen-time = screenTimeRule;
    systemd.tmpfiles.rules = [ "d /var/lib/nexus/screen-time 0755 root root -" ];
    systemd.services.nexus-screen-time = {
      description = "Screen time: count a minute for children signed in";
      unitConfig.ConditionDirectoryNotEmpty = "/var/lib/nexus/screen-time/limits";
      serviceConfig = { Type = "oneshot"; ExecStart = "${screenTime}/bin/nexus-screen-time tick"; };
    };
    systemd.timers.nexus-screen-time = {
      wantedBy = [ "timers.target" ];
      timerConfig = { OnCalendar = "minutely"; AccuracySec = "5s"; };
    };
  };
}
