# Secure Boot with this computer's own keys, keeping systemd-boot (so
# Guardian's one-time trial of a new version still works: stage_executor.py).
#
# nexusos.secureBoot.enable (off by default; turned on through Guardian from
# Settings, About) makes keys on this computer with sbctl (/var/lib/sbctl, root
# only) and signs systemd-boot and every kernel on the EFI partition each time
# the start-up menu is written. The menu's editor is off, so the kernel's
# command line cannot be changed at start-up. As with other distributions'
# Secure Boot, the initrd is not signed; the disk's encryption protects what is
# on it. fwupd's firmware updater is signed too, so firmware updates still work.
#
# Enrolling the keys is the one step that touches the firmware and needs it in
# Setup Mode: nexus-secure-boot-enroll (root, through pkexec) does it with
# Microsoft's keys and the firmware's own kept (Windows, graphics cards' option
# ROMs). scripts/boot/nexus_secure_boot.py has the steps.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.secureBoot;
  sbctl = pkgs.sbctl;
  esp = config.boot.loader.efi.efiSysMountPoint;
  helper = ../../scripts/boot/nexus_secure_boot.py;
  env = ''
    export NEXUS_SECURE_BOOT_PREPARED=${if cfg.enable then "1" else "0"}
    export NEXUS_SBCTL=${sbctl}/bin/sbctl
  '';
  commands = pkgs.runCommand "nexus-secure-boot" { } ''
    mkdir -p $out/bin
    cat > $out/bin/nexus-secure-boot <<EOF
    #!${pkgs.runtimeShell}
    ${env}
    exec ${pkgs.python3}/bin/python3 -I ${helper} status
    EOF
    cat > $out/bin/nexus-secure-boot-enroll <<EOF
    #!${pkgs.runtimeShell}
    ${env}
    exec ${pkgs.python3}/bin/python3 -I ${helper} enroll
    EOF
    chmod +x $out/bin/*
  '';
  policy = pkgs.writeTextDir "share/polkit-1/actions/org.nexusos.secureboot.policy" ''
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
     "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
    <policyconfig>
      <vendor>NexusOS</vendor>
      <action id="org.nexusos.secureboot.enroll">
        <description>Put this computer's Secure Boot keys in the firmware</description>
        <message>An administrator's password is needed to put this computer's Secure Boot keys in the firmware.</message>
        <defaults>
          <allow_any>no</allow_any>
          <allow_inactive>no</allow_inactive>
          <allow_active>auth_admin</allow_active>
        </defaults>
        <annotate key="org.freedesktop.policykit.exec.path">/run/current-system/sw/bin/nexus-secure-boot-enroll</annotate>
      </action>
    </policyconfig>
  '';
  # Run after systemd-boot writes the menu: every loader and kernel on the EFI
  # partition signed. A failure stops the switch, so a signed machine never gets
  # an unsigned kernel.
  sign = ''
    (
    set -eu
    sbctl=${sbctl}/bin/sbctl
    [ -d /var/lib/sbctl/keys ] || "$sbctl" create-keys
    for f in "${esp}"/EFI/systemd/systemd-boot*.efi "${esp}"/EFI/BOOT/BOOT*.EFI "${esp}"/EFI/nixos/*-bzImage.efi; do
      # Skipped by sbctl when it is already signed with these keys.
      if [ -f "$f" ]; then "$sbctl" sign "$f"; fi
    done
    )
  '';
in
{
  options.nexusos.secureBoot.enable = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "Sign the start-up menu and kernels with this computer's own Secure Boot keys (Settings, About).";
  };

  config = lib.mkMerge [
    { environment.systemPackages = [ commands policy ]; }
    (lib.mkIf cfg.enable {
      assertions = [{
        assertion = config.boot.loader.systemd-boot.enable;
        message = "nexusos.secureBoot needs systemd-boot.";
      }];
      environment.systemPackages = [ sbctl ];
      boot.loader.systemd-boot.editor = lib.mkForce false;
      boot.loader.systemd-boot.extraInstallCommands = sign;
      # fwupd's capsule updater, signed with the same keys.
      systemd.services.fwupd.environment.FWUPD_EFIAPPDIR = "/run/fwupd-efi";
      systemd.services.fwupd-efi = lib.mkIf config.services.fwupd.enable {
        description = "Sign fwupd's EFI updater for Secure Boot";
        wantedBy = [ "fwupd.service" ];
        partOf = [ "fwupd.service" ];
        before = [ "fwupd.service" ];
        unitConfig.ConditionPathIsDirectory = "/var/lib/sbctl/keys";
        serviceConfig = { Type = "oneshot"; RemainAfterExit = true; RuntimeDirectory = "fwupd-efi"; };
        script = ''
          for f in ${config.services.fwupd.package.fwupd-efi}/libexec/fwupd/efi/fwupd*.efi; do
            ${sbctl}/bin/sbctl sign -o "/run/fwupd-efi/$(basename "$f").signed" "$f"
          done
        '';
      };
      services.fwupd.uefiCapsuleSettings.DisableShimForSecureBoot = lib.mkIf config.services.fwupd.enable true;
    })
  ];
}
