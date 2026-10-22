# nexus-install end to end: from the installer environment, erase a blank
# UEFI disk, install NexusOS on it, then boot that disk and check the person
# can log in to the NexusOS desktop. Modelled on nixpkgs' installer tests: the
# installer runs from a small second disk, and the target machine boots the
# same main disk image afterwards. The VM has no network, so everything the
# install builds must already be in its store.
{ pkgs, nexusSource, nixpkgsSource, referenceSystem, nexusInstall }:
let
  common = {
    virtualisation.diskSize = 16 * 1024;
    virtualisation.diskImage = "./target.qcow2";
    virtualisation.memorySize = 4096;
    virtualisation.cores = 4;
    virtualisation.useEFIBoot = true;
  };
in
pkgs.testers.runNixOSTest {
  name = "nexus-installer";
  # installation-device.nix adds a nixpkgs overlay.
  node.pkgsReadOnly = false;
  nodes.installer = { modulesPath, lib, pkgs, ... }: {
    imports = [
      common
      "${modulesPath}/profiles/installation-device.nix"
      "${modulesPath}/profiles/base.nix"
    ];
    # Format the installer's own small root disk on first boot (systemd initrd).
    virtualisation.fileSystems."/".autoFormat = true;
    virtualisation.emptyDiskImages = [ 1024 ];
    virtualisation.rootDevice = "/dev/vdb";
    hardware.enableAllFirmware = lib.mkForce false;

    environment.etc."nexusos".source = nexusSource;
    environment.systemPackages = [ nexusInstall ];
    nix.settings = {
      experimental-features = [ "nix-command" "flakes" ];
      substituters = lib.mkForce [ ];
      hashed-mirrors = null;
      connect-timeout = 1;
    };
    # What the parts built for this machine's hardware need, from nixpkgs'
    # installer test, plus the installed system itself and nixpkgs' source.
    system.extraDependencies = with pkgs; [
      referenceSystem nixpkgsSource
      stdenv bintools brotli brotli.dev brotli.lib desktop-file-utils docbook5 docbook_xsl_ns
      kbd.dev kmod.dev libarchive.dev libxml2.bin libxslt.bin nixos-rebuild-ng ntp
      perlPackages.ConfigIniFiles perlPackages.FileSlurp perlPackages.JSON perlPackages.ListCompare
      perlPackages.XMLLibXML (python3.withPackages (p: [ p.mistune ])) shared-mime-info sudo
      switch-to-configuration-ng texinfo unionfs-fuse lndir shellcheck-minimal systemdMinimal.out
      curl zstd.bin mypy bootspec
    ];
  };

  nodes.target = { lib, ... }: {
    imports = [ common ];
    virtualisation.useBootLoader = true;
    virtualisation.useDefaultFilesystems = false;
    virtualisation.efi.keepVariables = false;
    virtualisation.fileSystems."/" = {
      device = "/dev/disk/by-label/this-is-not-real-and-will-never-be-used";
      fsType = "ext4";
    };
  };

  testScript = ''
    installer.start()
    installer.wait_for_unit("multi-user.target")

    with subtest("the installer refuses a partition and an unknown option"):
        installer.fail("nexus-install --disk /dev/vda1 --user alice --yes")
        installer.fail("nexus-install --bogus")

    with subtest("nexus-install puts NexusOS on the blank disk"):
        installer.succeed("echo 'nexus-test-pw' > /tmp/pw")
        # The test talks to the installed system through the test backdoor,
        # as nixpkgs' installer tests do.
        installer.succeed("""cat > /tmp/test.nix <<'EOF'
    { modulesPath, ... }: {
      imports = [ "''${modulesPath}/testing/test-instrumentation.nix" "''${modulesPath}/profiles/qemu-guest.nix" ];
    }
    EOF""")
        installer.succeed(
            "nexus-install --disk /dev/vda --user alice --full-name 'Alice Test'"
            " --password-file /tmp/pw --hostname nexus-test --timezone Europe/London"
            " --extra-module /tmp/test.nix --yes >&2",
            timeout=7200)
        installer.succeed("test -f /mnt/etc/nixos/flake.lock && test -f /mnt/etc/nixos/nexusos/flake.nix")
        installer.succeed("grep -q 'nexusos.user = \"alice\"' /mnt/etc/nixos/local.nix")
        installer.succeed("umount -R /mnt")
        installer.succeed("sync")
        installer.shutdown()

    # The same machine, now booting from the disk it installed to.
    target.state_dir = installer.state_dir
    target.start()

    with subtest("the installed system boots as NexusOS"):
        target.wait_for_unit("multi-user.target")
        target.succeed("grep -q '^NAME=NexusOS' /etc/os-release")
        assert "nexus-test" in target.succeed("hostname")
        target.succeed("test -e /boot/loader/loader.conf")

    with subtest("alice exists, with her password set and sudo through wheel"):
        target.succeed("id alice | grep -q wheel")
        target.succeed("grep -q '^alice:\\$' /etc/shadow")

    with subtest("the desktop, Core and local inference are installed"):
        target.wait_for_unit("greetd.service")
        target.wait_for_unit("ollama.service")
        target.succeed("test -x /run/current-system/sw/bin/nexus-shell")
        target.succeed("test -f /etc/nexus/hyprland.conf")

    with subtest("the machine can rebuild itself from /etc/nixos"):
        target.succeed("nix --extra-experimental-features 'nix-command flakes' eval --raw /etc/nixos#nixosConfigurations.nexus.config.networking.hostName | grep -qx nexus-test")
  '';
}
