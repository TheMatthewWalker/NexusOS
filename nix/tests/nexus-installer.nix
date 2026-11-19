# nexus-install end to end: from the installer environment, erase a blank
# UEFI disk, install NexusOS on it, then boot that disk and check the person
# can log in to the NexusOS desktop. Modelled on nixpkgs' installer tests: the
# installer runs from a small second disk, and the target machine boots the
# same main disk image afterwards. The VM has no network, so everything the
# install builds must already be in its store.
{ pkgs, nexusSource, nixpkgsSource, referenceSystem, nexusInstall, referenceInitrdInputs ? [ ], nexusRelease ? null }:
let
  common = {
    virtualisation.diskSize = 16 * 1024;
    virtualisation.diskImage = "./target.qcow2";
    virtualisation.memorySize = 4096;
    virtualisation.cores = 4;
    virtualisation.useEFIBoot = true;
  };
  # A tiny stand-in for the model the image can carry (nix/packages/ollama-model.nix):
  # a real Ollama layout and manifest, with small blobs.
  blob = text: { digest = "sha256:" + builtins.hashString "sha256" text; size = builtins.stringLength text; file = pkgs.writeText "blob" text; };
  modelConfig = blob (builtins.toJSON { model_format = "gguf"; model_family = "llama"; model_families = [ "llama" ]; model_type = "1M"; file_type = "Q4_K_M"; });
  modelWeights = blob "not really a model";
  testModel = (pkgs.callPackage ../packages/ollama-model.nix { }).build {
    name = "nexus-test"; tag = "tiny";
    manifest = builtins.toJSON {
      schemaVersion = 2;
      mediaType = "application/vnd.docker.distribution.manifest.v2+json";
      config = { mediaType = "application/vnd.docker.container.image.v1+json"; inherit (modelConfig) digest size; };
      layers = [ { mediaType = "application/vnd.ollama.image.model"; inherit (modelWeights) digest size; } ];
    };
    blobs = { ${modelConfig.digest} = modelConfig.file; ${modelWeights.digest} = modelWeights.file; };
  };
in
pkgs.testers.runNixOSTest {
  name = "nexus-installer";
  # Installing builds the hardware-specific parts with no network, which takes
  # under an hour with KVM but longer under software emulation.
  globalTimeout = 4 * 3600;
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
    environment.etc."nexusos-model".source = testModel;
    environment.systemPackages = [ nexusInstall pkgs.jq ];
    # The installer image's Nix settings, unchanged: it lists cache.nixos.org,
    # which this VM cannot reach, as a laptop with no Wi-Fi cannot.
    nix.settings.experimental-features = [ "nix-command" "flakes" ];
    # The same store contents as the installer image (nix/installer/iso.nix):
    # the installed system, nixpkgs' source and the offline build tools.
    system.extraDependencies = [ referenceSystem nixpkgsSource ] ++ import ../installer/offline-deps.nix pkgs
      ++ referenceInitrdInputs;
    # As the image: the release this NexusOS is, when built from a commit (CI), so
    # the install follows NexusOS's releases (nexus-install's follow_release).
    environment.etc."nexusos-release.json" = pkgs.lib.mkIf (nexusRelease != null && nexusSource ? rev) {
      text = builtins.toJSON (nexusRelease // { inherit (nexusSource) rev lastModified narHash; });
    };
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
        # An unknown keyboard layout is refused before the disk is touched.
        out = installer.fail("echo 'nexus-test-pw' | nexus-install --disk /dev/vda --user alice --password-file - --keyboard xx --yes 2>&1")
        assert "unknown keyboard layout xx" in out, out

    with subtest("the graphical installer's probe sees the disk, and alongside needs room beside something"):
        out = installer.succeed("nexus-install --probe-json")
        assert '"event":"probe"' in out and '"path":"/dev/vda"' in out, out
        assert '"possible":false' in out and "no partitions" in out, out
        installer.fail("nexus-install --alongside --user alice --yes")
        out = installer.fail("echo 'nexus-test-pw' | nexus-install --progress-json --alongside --disk /dev/vda"
                             " --user alice --password-file - --yes")
        assert '"event":"error"' in out and "no partitions" in out, out

    with subtest("after a failed attempt, trying again works"):
        installer.succeed("echo 'nexus-test-pw' > /tmp/pw")
        installer.succeed("echo '{ ... }: throw \"a failed first attempt\"' > /tmp/broken.nix")
        installer.fail(
            "nexus-install --disk /dev/vda --user alice --password-file /tmp/pw"
            " --extra-module /tmp/broken.nix --yes >&2",
            timeout=1800)
        # That attempt left the disk mounted at /mnt; the next one must not refuse it.
        installer.succeed("findmnt /mnt")

    with subtest("nexus-install puts NexusOS on the blank disk"):
        installer.succeed("echo 'nexus-test-pw' > /tmp/pw")
        # The test talks to the installed system through the test backdoor,
        # as nixpkgs' installer tests do.
        installer.succeed("""cat > /tmp/test.nix <<'EOF'
    { modulesPath, ... }: {
      imports = [ "''${modulesPath}/testing/test-instrumentation.nix" "''${modulesPath}/profiles/qemu-guest.nix" ];
      # Boot the default entry at once: in the test harness the boot menu's
      # countdown can stop and wait for a key that never comes.
      boot.loader.timeout = 0;
      # The serial line last, so it is the console the initrd asks for the
      # disk's password on, and the test can answer there. The start-up
      # screen (Plymouth) would ask on the display instead: it is still built
      # into the initrd during the offline install, and only not started here.
      boot.kernelParams = [ "console=ttyS0,115200" "plymouth.enable=0" ];
    }
    EOF""")
        installer.succeed(
            "nexus-install --disk /dev/vda --user alice --full-name 'Alice Test'"
            " --password-file /tmp/pw --hostname nexus-test --timezone Europe/London --keyboard gb"
            " --extra-module /tmp/test.nix --model nexus-test:tiny --yes >&2",
            timeout=7200)
        installer.succeed("test -f /mnt/etc/nixos/flake.lock && test -f /mnt/etc/nixos/nexusos/flake.nix")
        installer.succeed("grep -q 'nexusos.user = \"alice\"' /mnt/etc/nixos/local.nix")
        # A UK keyboard, everywhere: it also types the password at start-up (the
        # test's password, letters and a dash, is the same on both layouts).
        installer.succeed("grep -q 'nexusos.keyboard.layout = \"gb\"' /mnt/etc/nixos/local.nix")
        # Built from a commit (CI): the installed system follows NexusOS's release branch,
        # pinned to this commit, with nothing downloaded (this VM has no network).
        if installer.succeed("test -f /etc/nexusos-release.json && echo yes || echo no").strip() == "yes":
            installer.succeed("grep -q 'inputs.nexusos.url = \"github:TheMatthewWalker/NexusOS/release\"' /mnt/etc/nixos/flake.nix")
            installer.succeed("jq -e '.nodes.nexusos.locked.type == \"github\"' /mnt/etc/nixos/flake.lock")
        # The hardware scan: no graphics card in this VM, so the model runs on the
        # processor; the model asked for is the one written down and copied.
        installer.succeed("grep -q 'nexusos.hardware.gpu = \"none\"' /mnt/etc/nixos/local.nix")
        installer.succeed("grep -qE 'nexusos.hardware.memoryGiB = [0-9]+;' /mnt/etc/nixos/local.nix")
        installer.succeed("grep -q 'services.nexus-core.ollamaModel = \"nexus-test:tiny\"' /mnt/etc/nixos/local.nix")
        installer.succeed("test -f /mnt/var/lib/nexus-model-seed/manifests/registry.ollama.ai/library/nexus-test/tiny")
        installer.succeed("umount -R /mnt")
        installer.succeed("sync")
        installer.shutdown()

    # The same machine, now booting from the disk it installed to.
    target.state_dir = installer.state_dir
    target.start()

    with subtest("the encrypted disk opens with alice's password"):
        target.wait_for_console_text("passphrase for disk", timeout=600)
        target.send_console("nexus-test-pw\n")

    with subtest("the installed system boots as NexusOS"):
        target.wait_for_unit("multi-user.target")
        target.succeed("grep -q '^NAME=NexusOS' /etc/os-release")
        assert "nexus-test" in target.succeed("hostname")
        target.succeed("test -e /boot/loader/loader.conf")
        # The root filesystem is inside LUKS2, which the hardware scan wrote down.
        target.succeed("cryptsetup status nexusos-root | grep -q 'type:    LUKS2'")
        target.succeed("findmnt -no SOURCE / | grep -qx /dev/mapper/nexusos-root")
        target.succeed("grep -q 'boot.initrd.luks.devices' /etc/nixos/hardware-configuration.nix")

    with subtest("alice exists, with her password set and sudo through wheel"):
        target.succeed("id alice | grep -q wheel")
        target.succeed("grep -q '^alice:\\$' /etc/shadow")

    with subtest("the desktop, Core and local inference are installed"):
        target.wait_for_unit("greetd.service")
        target.wait_for_unit("ollama.service")
        target.succeed("test -x /run/current-system/sw/bin/nexus-shell")
        target.succeed("test -f /etc/nexus/hyprland.conf")
        target.succeed("grep -q 'kb_layout = gb' /etc/nexus/hyprland.conf")
        target.succeed("grep -q 'KEYMAP=uk' /etc/vconsole.conf")
        target.succeed("grep -q '^XKB_DEFAULT_LAYOUT=gb$' /etc/nexus/greeter.env")
        # The start-up screen is installed with its theme.
        target.succeed("test -f /etc/plymouth/themes/nexusos/nexusos.plymouth")
        target.succeed("grep -q 'Theme=nexusos' /etc/plymouth/plymouthd.conf")
        # The login screen's greeter account is a system user: Core must not run for it.
        target.fail("journalctl -b --no-pager | grep -q 'nexus-core.*Unhandled exception'")

    with subtest("the model from the image is handed to Ollama, with no download"):
        target.wait_until_succeeds("journalctl -b -u nexus-model-pull | grep -q 'Moved the model from the installer'", timeout=600)
        target.succeed("test ! -e /var/lib/nexus-model-seed")
        manifest = "/var/lib/ollama/models/manifests/registry.ollama.ai/library/nexus-test/tiny"
        target.succeed(f"test -f {manifest}")
        # Owned by Ollama's (dynamic) user, so Ollama can add and remove models later.
        assert target.succeed(f"stat -L -c %u {manifest}") == target.succeed("stat -L -c %u /var/lib/ollama"), "wrong owner"
        assert target.succeed("stat -L -c %u /var/lib/ollama").strip() != "0", "Ollama's directory is owned by root"
        target.wait_until_succeeds("OLLAMA_HOST=127.0.0.1:11434 ollama list | grep -q 'nexus-test:tiny'", timeout=300)

    with subtest("Guardian is on, with this computer's own builder key made at first boot"):
        for unit in ("nexus-guardian-keys", "nexus-guardian", "nexus-guardian-builder", "nexus-guardian-stage",
                     "nexus-guardian-boot-check"):
            target.wait_for_unit(unit + ".service")
        assert target.succeed("stat -c '%a %U' /var/lib/nexus-guardian-builder").strip() == "700 root"
        assert target.succeed("stat -c '%a %U' /var/lib/nexus-guardian-builder/builder.key").strip() == "600 root"
        # The broker trusts exactly this computer's key (no key is configured in the image).
        target.succeed("diff <(grep -v '^#' /var/lib/nexus-guardian-builder/trusted-keys) /var/lib/nexus-guardian-builder/builder.pub")
        for sock in ("nexus-guardian/review", "nexus-guardian-builder/build", "nexus-guardian-stage/stage"):
            assert target.succeed(f"stat -c '%a %U %G' /run/{sock}.sock").strip() == "660 root nexus-guardian", sock
        target.succeed("id -nG alice | grep -qw nexus-guardian")
        # Alice's own requests are answered (here: no such candidate); the approval
        # helper is polkit's, and trial boots fall back by themselves.
        out = target.succeed("su - alice -c \"nexus-guardian-client status '{\\\"candidateId\\\": \\\"" + "0" * 24 + "\\\"}'\" || true")
        assert "No such candidate" in out, out
        target.succeed("grep -q org.nexusos.guardian.approve /run/current-system/sw/share/polkit-1/actions/org.nexusos.guardian.policy")
        target.succeed("test -x /run/current-system/sw/bin/nexus-guardian-desktop-approve")
        # Only the trial-boot entry resets on a failed boot (Guardian's panic=10).
        # Not boot.panic_on_fail or /proc/cmdline: the test instrumentation in
        # this system adds boot.panic_on_fail and panic=1 to every entry.
        trial_entry = "/boot/loader/entries/nixos-generation-1-specialisation-guardian-trial.conf"
        target.succeed("grep -qw panic=10 /run/current-system/specialisation/guardian-trial/kernel-params")
        target.succeed(f"grep '^options' {trial_entry} | grep -qw panic=10")
        target.succeed("grep -qx 'default nixos-generation-1.conf' /boot/loader/loader.conf")
        target.fail("grep -qw panic=10 /run/current-system/kernel-params")
        target.fail("grep -qw panic=10 /boot/loader/entries/nixos-generation-1.conf")
        target.succeed("journalctl -b -u nexus-guardian-boot-check | grep -q nothing-pending")

    with subtest("the machine can rebuild itself from /etc/nixos"):
        target.succeed("nix --extra-experimental-features 'nix-command flakes' eval --raw /etc/nixos#nixosConfigurations.nexus.config.networking.hostName | grep -qx nexus-test")
  '';
}
