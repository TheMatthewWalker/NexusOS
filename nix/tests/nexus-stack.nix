# Full stack in a NixOS guest: per-user Nexus Core services talking to the root
# Guardian broker over their real sockets, with crash recovery under systemd.
{ pkgs, guardianModule, coreModule }:
pkgs.testers.runNixOSTest {
  name = "nexus-stack";
  nodes.machine = { lib, ... }: {
    imports = [ guardianModule coreModule ];
    system.stateVersion = "26.05";
    # Test VMs omit switch-to-configuration by default; real systems have it and
    # the broker refuses toplevels that cannot be staged.
    system.switch.enable = true;
    virtualisation.memorySize = 2048;
    users.users.alice = { isNormalUser = true; linger = true; };
    users.users.mallory = { isNormalUser = true; linger = true; };
    services.nexus-core.enable = true;
    services.nexus-guardian = {
      enable = true;
      allowedUsers = [ "alice" ];
      hostConfig = "path:/etc/nixos#nixosConfigurations.machine";
      trustedBuilderKeys = [ (lib.removeSuffix "\n" (builtins.readFile ./lab-builder.pub)) ];
    };
    environment.systemPackages = [ pkgs.curl ];
  };
  testScript = ''
    import json, shlex

    def as_user(user, command, check=True):
        status, out = machine.execute("su - " + user + " -c " + shlex.quote(command))
        if check:
            assert status == 0, f"{user}: {command}: {out}"
        return status, out

    def api(user, method, path, body=None):
        sock = f"/home/{user}/.local/state/nexusos/nexus.sock"
        token = f"$(cat /home/{user}/.local/state/nexusos/api-token)"
        data = "" if body is None else " -H 'Content-Type: application/json' -d " + shlex.quote(json.dumps(body))
        _, out = as_user(user, f"curl -s -w '\\n%{{http_code}}' -X {method} --unix-socket {sock} -H \"Authorization: Bearer {token}\"{data} http://nexus{path}")
        body_text, code = out.rsplit("\n", 1)
        return int(code), body_text

    machine.start()
    machine.succeed("nix-env --profile /nix/var/nix/profiles/system --set $(readlink -f /run/current-system)")
    machine.wait_for_unit("nexus-guardian.service")
    for user in ("alice", "mallory"):
        machine.wait_for_unit("nexus-core.service", user)
        machine.wait_for_file(f"/home/{user}/.local/state/nexusos/nexus.sock")

    with subtest("Each user's Core is private and answers its own CLI"):
        assert '"status": "ok"' in as_user("alice", "nexus health")[1]
        assert machine.succeed("stat -c %a /home/alice/.local/state/nexusos").strip() == "700"
        status, _ = as_user("mallory", "curl -s --unix-socket /home/alice/.local/state/nexusos/nexus.sock http://nexus/health", check=False)
        assert status != 0, "mallory reached alice's Core socket"
        machine.fail("ss -ltn | grep -q ':5077 '")

    with subtest("Alice's Core submits through the root broker"):
        code, body = api("alice", "POST", "/guardian/submit",
                         {"description": "Enable printing", "changes": [{"option": "services.printing.enable", "enabled": True}]})
        assert code == 200, body
        response = json.loads(body)
        assert response["planId"] == response["broker"]["candidate"]["planId"]
        assert response["broker"]["candidate"]["baseline"] == machine.succeed("readlink -f /run/current-system").strip()

    with subtest("Mallory's Core is refused by the broker"):
        code, body = api("mallory", "POST", "/guardian/submit",
                         {"description": "x", "changes": [{"option": "services.printing.enable", "enabled": True}]})
        assert code == 403, (code, body)

    with subtest("A crashed Core is restarted by systemd despite the stale socket"):
        machine.succeed("su - alice -c 'kill -9 $(systemctl --user show -p MainPID --value nexus-core)'")
        machine.wait_until_succeeds("su - alice -c 'nexus health' | grep -q ok", timeout=120)
        assert machine.succeed("su - alice -c 'systemctl --user show -p NRestarts --value nexus-core'").strip() != "0"
  '';
}
