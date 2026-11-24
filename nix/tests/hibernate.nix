# Hibernate (nix/modules/hibernate.nix): the swap file is made at start-up
# the size of the memory, the machine hibernates into it and comes back with
# what was only in memory, and a cold start afterwards does not resume a
# stale image. As NexusOS installs: EFI, the systemd initrd, ext4 root, and
# no resume= kernel option (systemd's HibernateLocation EFI variable says
# where the image is).
{ pkgs, hibernateModule }:
pkgs.testers.runNixOSTest {
  name = "hibernate";
  nodes.machine = { lib, ... }: {
    imports = [ hibernateModule "${pkgs.path}/nixos/tests/common/auto-format-root-device.nix" ];
    system.stateVersion = "26.05";
    nexusos.hibernate.enable = true;
    virtualisation.memorySize = 1024;
    # The swap file (about 1.1 GB) plus the 5 GB the module keeps free.
    virtualisation.diskSize = 8192;
    virtualisation.useNixStoreImage = true;
    virtualisation.useEFIBoot = true;
    boot.initrd.systemd.enable = true;
    swapDevices = lib.mkOverride 0 [ ];
    zramSwap.enable = false;
    powerManagement.powerDownCommands = "systemctl --no-block stop backdoor.service";
    powerManagement.resumeCommands = "systemctl --no-block restart backdoor.service";
  };

  testScript = ''
    machine.wait_for_unit("nexus-hibernate-swap.service")
    machine.succeed("grep -q '^/var/lib/nexus/hibernate.swap ' /proc/swaps")
    size = int(machine.succeed("stat -c %s /var/lib/nexus/hibernate.swap").strip())
    mem = int(machine.succeed("awk '/^MemTotal:/ { print $2 * 1024 }' /proc/meminfo").strip())
    assert size >= mem, f"swap file {size} smaller than memory {mem}"
    machine.succeed("busctl call org.freedesktop.login1 /org/freedesktop/login1 org.freedesktop.login1.Manager CanHibernate | grep -q yes")

    # Something only in memory, then hibernate.
    machine.succeed("mkdir /run/test", "mount -t ramfs -o size=1m ramfs /run/test",
                    "echo only in memory > /run/test/marker")
    machine.execute("systemctl hibernate >&2 &", check_return=False)
    machine.wait_for_shutdown()

    # Resumed: it is still there.
    machine.start()
    machine.succeed("grep 'only in memory' /run/test/marker")

    # A cold start afterwards boots fresh.
    machine.crash()
    machine.wait_for_unit("multi-user.target")
    machine.fail("grep 'only in memory' /run/test/marker")
  '';
}
