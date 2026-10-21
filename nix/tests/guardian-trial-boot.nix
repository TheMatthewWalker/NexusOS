# End-to-end M4: Core-style submission -> root approval -> signed attestation ->
# staged ONE-TIME trial boot on a real GRUB disk. A healthy trial is confirmed;
# a broken trial panics, resets and falls back to the previous generation with
# no human input, and the boot check restores the profile.
{ pkgs, guardianModule }:
pkgs.testers.runNixOSTest {
  name = "nexus-guardian-trial-boot";
  nodes.machine = { config, lib, extendModules, ... }:
    let
      # Candidates are overlays on this exact configuration (ADR-002), built on
      # the host and shipped into the guest store. Variants switch off the
      # lab references below so extending does not recurse.
      variant = modules: (extendModules {
        modules = modules ++ [ { nexusLab.isVariant = true; } ];
      }).config.system.build.toplevel;
      drv = modules: builtins.unsafeDiscardStringContext (variant modules).drvPath;
      good = [ { services.printing.enable = true; } ];
      bad = [ ./trial-broken.nix { networking.networkmanager.enable = true; } ];
    in {
      options.nexusLab.isVariant = lib.mkOption { type = lib.types.bool; default = false; };
      imports = [ guardianModule ];
      config = lib.mkMerge [ {
      system.stateVersion = "26.05";
      virtualisation.useBootLoader = true;
      virtualisation.memorySize = 2048;
      boot.loader.grub.enable = true;
      boot.loader.grub.device = "/dev/vda";
      boot.loader.timeout = 3;
      boot.loader.grub.extraConfig = ''
        serial --unit=0 --speed=115200
        terminal_input console serial
        terminal_output console serial
      '';
      boot.kernelParams = [ "console=ttyS0,115200n8" ];
      users.users.alice.isNormalUser = true;
      services.nexus-guardian = {
        enable = true;
        allowedUsers = [ "alice" ];
        hostConfig = "path:/etc/nixos#nixosConfigurations.machine";
        trustedBuilderKeys = [ (lib.removeSuffix "\n" (builtins.readFile ./lab-builder.pub)) ];
        staging = { enable = true; panicRebootSeconds = 5; requiredUnits = [ "nexus-guardian.service" ]; };
      };
      environment.systemPackages = [ pkgs.util-linux ];
      environment.etc."nexus-lab/builder.key".source = ./lab-builder.key;
      } (lib.mkIf (!config.nexusLab.isVariant) {
        environment.etc."nexus-lab/candidate".text = "${variant good}";
        environment.etc."nexus-lab/broken".text = "${variant bad}";
        environment.etc."nexus-lab/candidate-drv".text = drv good;
        environment.etc."nexus-lab/broken-drv".text = drv bad;
        system.extraDependencies = [ (variant good) (variant bad) ];
      }) ];
    };
  testScript = ''
    import json, re, shlex

    def client(operation, payload, expect=0):
        command = "nexus-guardian-client " + operation + " " + shlex.quote(json.dumps(payload))
        status, out = machine.execute("su - alice -c " + shlex.quote(command))
        assert status == expect, f"{operation}: exit {status}: {out}"
        return json.loads(out.strip().splitlines()[-1])

    def approved_decision(option, toplevel, deriver):
        candidate = client("submit", {"description": "lab", "changes": [{"option": option, "enabled": True}]})["candidate"]
        cid = candidate["candidateId"]
        machine.succeed("echo " + shlex.quote("APPROVE " + cid) + " | script -qec "
                        + shlex.quote("nexus-guardian approve --candidate-id " + cid + " --user alice") + " /dev/null")
        machine.succeed("echo " + shlex.quote(json.dumps(candidate)) + " > /tmp/candidate.json")
        attestation = json.loads(machine.succeed(
            "nexus-guardian-attest sign --private-key /etc/nexus-lab/builder.key --candidate /tmp/candidate.json "
            f"--toplevel {toplevel} --drv-path {deriver} --builder lab --evaluate pass --build pass --vm-test pass"))
        decision = client("review", {"candidateId": cid, "attestation": attestation})
        assert decision["eligible"] is True, decision
        return decision["decision"]

    def events():
        return [json.loads(line)["event"] for line in
                machine.succeed("cat /var/lib/nexus-guardian/staging.jsonl").splitlines()]

    def current():
        return machine.succeed("readlink -f /run/current-system").strip()

    machine.start(allow_reboot=True)
    machine.wait_for_unit("multi-user.target")
    machine.wait_for_unit("nexus-guardian.service")
    baseline = current()
    candidate = machine.succeed("cat /etc/nexus-lab/candidate").strip()
    broken = machine.succeed("cat /etc/nexus-lab/broken").strip()
    candidate_drv = machine.succeed("cat /etc/nexus-lab/candidate-drv").strip()
    broken_drv = machine.succeed("cat /etc/nexus-lab/broken-drv").strip()
    assert "panic=5" in machine.succeed("cat /proc/cmdline")

    with subtest("Stage a healthy candidate for one trial boot"):
        decision = approved_decision("services.printing.enable", candidate, candidate_drv)
        machine.succeed("nexus-guardian-stage --decision " + decision)
        env = machine.succeed("grub-editenv /boot/grub/grubenv list")
        assert "next_entry=" in env and "saved_entry=" in env, env
        assert current() == baseline  # Nothing changed in the running system.
        machine.fail("nexus-guardian-stage --decision " + decision)  # Once only.

    with subtest("Healthy trial boot is confirmed and becomes the default"):
        machine.reboot()
        machine.wait_for_unit("multi-user.target")
        machine.wait_for_unit("nexus-guardian-boot-check.service")
        assert current() == candidate
        assert events()[-1] == "confirmed", events()
        machine.reboot()
        machine.wait_for_unit("multi-user.target")
        assert current() == candidate, "confirmed generation did not persist"

    with subtest("Broken trial boot panics and falls back with no human input"):
        decision = approved_decision("networking.networkmanager.enable", broken, broken_drv)
        machine.succeed("nexus-guardian-stage --decision " + decision)
        offset = len(machine.get_console_log())
        machine.reboot()  # Returns only once some generation boots and reconnects.
        machine.wait_for_unit("multi-user.target")
        machine.wait_for_unit("nexus-guardian-boot-check.service")
        serial = re.sub(r"\x1b\[[0-9;:]*[A-Za-z]", "", machine.get_console_log()[offset:])
        assert broken in serial, "serial log does not show the broken generation being tried"
        assert re.search(r"Kernel panic|panic_on_fail|Rebooting in", serial), "no panic/auto-reboot evidence"
        assert current() == candidate, "did not fall back to the last confirmed generation"
        assert events()[-1] == "fell-back", events()
        assert machine.succeed("readlink -f /nix/var/nix/profiles/system").strip() == candidate

    with subtest("Recovered system survives another reboot"):
        machine.reboot()
        machine.wait_for_unit("multi-user.target")
        assert current() == candidate
        machine.succeed("systemctl is-active nexus-guardian.service")
  '';
}
