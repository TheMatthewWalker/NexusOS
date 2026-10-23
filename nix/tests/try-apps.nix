# "Try before you install" in a NixOS guest: a normal user runs nexus-try on
# pkgs.hello, fetched through the system's nixpkgs registry (pointed at this
# test's nixpkgs and pre-loaded into the guest store, so it works offline).
# Checks the sandbox hides the real home, discards its own home, hands in a
# file read-only, can drop the network, and changes no profile.
{ pkgs, nixpkgs, tryAppsModule }:
pkgs.testers.runNixOSTest {
  name = "try-apps";
  nodes.machine = { lib, ... }: {
    imports = [ tryAppsModule ];
    system.stateVersion = "26.05";
    virtualisation.memorySize = 2048;
    users.users.alice.isNormalUser = true;
    # The app and the nixpkgs source it is evaluated from, already in the store.
    virtualisation.additionalPaths = [ pkgs.hello nixpkgs.outPath ];
    # A locked entry (with narHash), as on a flake-built system: evaluation is
    # cached after the first trial.
    nix.registry.nixpkgs.flake = nixpkgs;
    nix.settings = {
      experimental-features = [ "nix-command" "flakes" ];
      flake-registry = ""; # offline: never fetch the global registry
      substituters = lib.mkForce [ ];
    };
  };
  testScript = ''
    import shlex

    def as_alice(command, status=0, timeout=900):
        code, out = machine.execute("su - alice -c " + shlex.quote(command) + " 2>&1", timeout=timeout)
        assert code == status, f"{command}: exit {code}, expected {status}: {out}"
        return out

    # Every profile link and the running system: none of it may change.
    def profiles():
        listing = []
        for path in ["/nix/var/nix/profiles", "/home/alice/.nix-profile", "/home/alice/.local/state/nix"]:
            code, out = machine.execute(f"find {path} -type l -printf '%p -> %l\\n' | sort")
            listing.append(f"{path} [{code}]\n{out}")
        listing.append(machine.succeed("readlink -f /run/current-system"))
        return "\n".join(listing)

    machine.wait_for_unit("multi-user.target")
    assert machine.succeed("su - alice -c 'command -v nexus-try bwrap'").count("/") > 2
    as_alice("echo private > ~/real-secret.txt && echo 'quarterly report' > ~/report.txt")
    before = profiles()

    with subtest("names that are not plain nixpkgs attributes are refused before any fetch"):
        for bad in ["nixpkgs#hello", "github:evil/flake#x", "/nix/store/x-hello", "../hello", "hello;id", "let"]:
            out = as_alice("nexus-try " + shlex.quote(bad), status=2)
            assert "fetching" not in out, out

    with subtest("hello runs from the store and nothing is kept"):
        out = as_alice("nexus-try hello", timeout=2400)
        print(out)
        assert "Hello, world!" in out, out
        assert "Nothing was kept" in out, out
        assert "nexus try keep hello" in out, out

    def marked(out, marker):
        return next(line[len(marker):].strip() for line in out.splitlines() if line.startswith(marker))

    net = "echo NET:$(tail -n +3 /proc/net/dev | cut -d: -f1 | tr -d ' ' | sort | tr '\\n' ' ')"

    with subtest("offline, with one file: the real home is invisible and the sandbox home is discarded"):
        out = as_alice("nexus-try --no-network --file ~/report.txt --shell hello -- -c "
                       + shlex.quote("echo inside > ~/made-inside.txt; echo HOME:$(ls -A ~); echo TRIAL:$(ls -A ~/Trial); "
                                     "cat ~/real-secret.txt /home/alice/real-secret.txt 2>/dev/null || echo SECRET-HIDDEN; "
                                     "echo REPORT:$(cat ~/Trial/report.txt); "
                                     "if echo x >> ~/Trial/report.txt; then echo WRITE:yes; else echo WRITE:no; fi; "
                                     "echo PIDS:$(ls /proc | grep -c '^[0-9]'); " + net))
        print(out)
        assert sorted(marked(out, "HOME:").split()) == ["Trial", "made-inside.txt"], out
        assert marked(out, "TRIAL:") == "report.txt", out
        assert "private" not in out and "SECRET-HIDDEN" in out, out
        assert marked(out, "REPORT:") == "quarterly report", out
        assert marked(out, "WRITE:") == "no", out
        # New PID namespace: only the shell and its children are visible.
        assert int(marked(out, "PIDS:")) < 10, out
        assert marked(out, "NET:") == "lo", out
        assert "network off" in out, out
        machine.fail("test -e /home/alice/made-inside.txt")
        machine.succeed("grep -qx private /home/alice/real-secret.txt")
        machine.succeed("test \"$(cat /home/alice/report.txt)\" = 'quarterly report'")

    with subtest("online by default, and the app takes arguments"):
        out = as_alice("nexus-try --shell hello -- -c " + shlex.quote("hello --greeting=sandboxed; " + net))
        print(out)
        assert "sandboxed" in out, out
        assert len(marked(out, "NET:").split()) > 1, out

    with subtest("no profile changed and hello is not installed"):
        after = profiles()
        assert before == after, f"profiles changed:\n{before}\n---\n{after}"
        as_alice("command -v hello", status=1)
  '';
}
