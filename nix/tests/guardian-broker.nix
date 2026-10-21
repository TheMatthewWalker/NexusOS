# Guardian broker as a real root systemd service in a disposable NixOS guest.
{ pkgs, guardianModule }:
pkgs.testers.runNixOSTest {
  name = "nexus-guardian-broker";
  nodes.machine = { pkgs, ... }: {
    imports = [ guardianModule ];
    system.stateVersion = "26.05";
    # Test VMs omit switch-to-configuration by default; real systems have it and
    # the broker refuses toplevels that cannot be staged.
    system.switch.enable = true;
    virtualisation.memorySize = 1536;
    users.users.alice = { isNormalUser = true; };    # allowed client
    users.users.mallory = { isNormalUser = true; };  # no group, no allowlist
    users.users.eve = { isNormalUser = true; extraGroups = [ "nexus-guardian" ]; };  # group only
    services.nexus-guardian = {
      enable = true;
      allowedUsers = [ "alice" ];
      hostConfig = "path:/etc/nixos#nixosConfigurations.machine";
      trustedBuilderKeys = [ (pkgs.lib.removeSuffix "\n" (builtins.readFile ./lab-builder.pub)) ];
    };
    environment.etc."nexus-lab/builder.key".source = ./lab-builder.key;
    environment.systemPackages = [ pkgs.util-linux ];
  };
  testScript = { nodes, ... }: ''
    import json, shlex

    def client(user, operation, payload, expect=0):
        command = "nexus-guardian-client " + operation + " " + shlex.quote(json.dumps(payload))
        status, out = machine.execute("su - " + user + " -c " + shlex.quote(command))
        assert status == expect, f"{user} {operation}: exit {status}: {out}"
        return json.loads(out.strip().splitlines()[-1])

    def approve(candidate_id):
        # The real interactive root-console path, driven through a pseudo-terminal.
        machine.succeed("echo " + shlex.quote("APPROVE " + candidate_id) + " | script -qec "
                        + shlex.quote("nexus-guardian approve --candidate-id " + candidate_id + " --user alice") + " /dev/null")

    def attest(candidate, key="/etc/nexus-lab/builder.key", build="pass"):
        machine.succeed("echo " + shlex.quote(json.dumps(candidate)) + " > /tmp/candidate.json")
        toplevel = machine.succeed("readlink -f /run/current-system").strip()
        # The builder knows the derivation it built; paths copied into the
        # guest report "unknown-deriver", which the broker accepts.
        deriver = "${builtins.unsafeDiscardStringContext nodes.machine.system.build.toplevel.drvPath}"
        return json.loads(machine.succeed(
            f"nexus-guardian-attest sign --private-key {key} --candidate /tmp/candidate.json --toplevel {toplevel} "
            f"--drv-path {deriver} --builder lab --evaluate pass --build {build} --vm-test pass"))

    machine.start()
    # Test guests boot without a system profile; an installed NixOS always has
    # one pointing at the running generation, which the broker verifies.
    machine.succeed("nix-env --profile /nix/var/nix/profiles/system --set $(readlink -f /run/current-system)")
    machine.wait_for_unit("nexus-guardian.service")
    machine.wait_for_file("/run/nexus-guardian/review.sock")

    with subtest("Ownership, permissions and sandbox"):
        assert machine.succeed("stat -c '%U %a' /var/lib/nexus-guardian").strip() == "root 700"
        assert machine.succeed("stat -c '%U %a' /var/lib/nexus-guardian/guardian.sqlite3").strip() == "root 600"
        assert machine.succeed("stat -c '%U %G %a' /run/nexus-guardian").strip() == "root nexus-guardian 750"
        assert machine.succeed("stat -c '%U %G %a' /run/nexus-guardian/review.sock").strip() == "root nexus-guardian 660"
        props = machine.succeed("systemctl show nexus-guardian -p NoNewPrivileges -p CapabilityBoundingSet "
                                "-p RestrictAddressFamilies -p PrivateNetwork -p ProtectSystem -p MemoryMax -p TasksMax")
        for expected in ("NoNewPrivileges=yes", "CapabilityBoundingSet=\n", "RestrictAddressFamilies=AF_UNIX",
                         "PrivateNetwork=yes", "ProtectSystem=strict", "TasksMax=16"):
            assert expected in props + "\n", f"missing {expected!r} in {props}"

    with subtest("An unverifiable running system refuses submissions"):
        machine.succeed("mv /nix/var/nix/profiles/system /nix/var/nix/profiles/system.saved")
        refused = client("alice", "submit", {"description": "x", "changes": [{"option": "services.printing.enable", "enabled": True}]}, expect=1)
        assert "could not be verified" in refused["error"]
        machine.succeed("mv /nix/var/nix/profiles/system.saved /nix/var/nix/profiles/system")

    with subtest("Callers outside the group cannot reach the socket"):
        response = client("mallory", "status", {"candidateId": "a" * 24}, expect=3)
        assert "permission denied" in response["error"]

    with subtest("Group members outside the allowlist are rejected by SO_PEERCRED"):
        response = client("eve", "status", {"candidateId": "a" * 24}, expect=4)
        assert "no response" in response["error"]

    with subtest("Allowed user submits; broker binds the observed generation"):
        submitted = client("alice", "submit", {"description": "Enable printing",
                                              "changes": [{"option": "services.printing.enable", "enabled": True}]})
        candidate = submitted["candidate"]
        assert candidate["baseline"] == machine.succeed("readlink -f /run/current-system").strip()
        assert candidate["activation"] == "prohibited"
        cid = candidate["candidateId"]

    with subtest("Review requires a root approval"):
        attestation = attest(candidate)
        denied = client("alice", "review", {"candidateId": cid, "attestation": attestation}, expect=1)
        assert "approval" in denied["error"]

    with subtest("Root console approval, then an eligible decision for the exact toplevel"):
        machine.fail("echo 'APPROVE wrong' | script -qec " + shlex.quote(
            "nexus-guardian approve --candidate-id " + cid + " --user alice") + " /dev/null")
        approve(cid)
        decision = client("alice", "review", {"candidateId": cid, "attestation": attestation})
        assert decision["eligible"] is True and decision["executed"] is False, decision
        assert decision["toplevel"] == attestation["statement"]["toplevel"]

    with subtest("Replay is refused; forged and failed attestations are not eligible"):
        replay = client("alice", "review", {"candidateId": cid, "attestation": attestation}, expect=1)
        assert "approval" in replay["error"]
        machine.succeed("nexus-guardian-attest keygen --private-key /root/rogue.key")
        approve(cid)
        forged = client("alice", "review", {"candidateId": cid, "attestation": attest(candidate, key="/root/rogue.key")})
        assert forged["eligible"] is False
        assert "attestation signature" in machine.succeed("journalctl -u nexus-guardian --no-pager")
        approve(cid)
        failed = client("alice", "review", {"candidateId": cid, "attestation": attest(candidate, build="fail")})
        assert failed["eligible"] is False

    with subtest("Decisions persist across a service restart"):
        machine.succeed("systemctl restart nexus-guardian")
        machine.wait_for_unit("nexus-guardian.service")
        machine.wait_for_file("/run/nexus-guardian/review.sock")
        status = client("alice", "status", {"candidateId": cid})
        assert status["decision"]["eligible"] is False  # Latest decision was the failed build.
        assert machine.succeed("stat -c '%a' /run/nexus-guardian/review.sock").strip() == "660"
  '';
}
