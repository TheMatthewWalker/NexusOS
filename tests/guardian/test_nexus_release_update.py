"""NexusOS updates: a computer installed from a release follows NexusOS's release branch, and an update moves it
forward there (with the nixpkgs that release brings), through the same lock-only path as security updates."""
import json
import os
from pathlib import Path
import unittest

import support  # noqa: F401  (puts scripts/guardian on the import path)

import system_update
from attestation import validate_update
from system_update import UpdateError
from update_check import check as update_check, follows_release
from test_system_update import Machine, fetcher

R1, R2 = "1" * 40, "2" * 40
N1, N2 = "a" * 40, "b" * 40


def release_lock(rev=R1, modified=1791000000, nixpkgs=N1, nix_modified=1790000000, branch="nixos-26.05",
                 owner="TheMatthewWalker", ref="release", extra_input=False):
    nexus_inputs = {"nixpkgs": "nixpkgs"}
    nodes = {
        "nexusos": {"inputs": nexus_inputs,
                    "locked": {"lastModified": modified, "narHash": "sha256-" + rev[:8], "owner": owner,
                               "repo": "NexusOS", "rev": rev, "type": "github"},
                    "original": {"owner": owner, "ref": ref, "repo": "NexusOS", "type": "github"}},
        "nixpkgs": {"locked": {"lastModified": nix_modified, "narHash": "sha256-" + nixpkgs[:8], "owner": "NixOS",
                               "repo": "nixpkgs", "rev": nixpkgs, "type": "github"},
                    "original": {"owner": "NixOS", "ref": branch, "repo": "nixpkgs", "type": "github"}},
        "root": {"inputs": {"nexusos": "nexusos"}},
    }
    if extra_input:   # a newer release with an input of its own
        nexus_inputs["extra"] = "extra"
        nodes["extra"] = {"locked": {"type": "github", "owner": "x", "repo": "y", "rev": "c" * 40, "lastModified": 1,
                                     "narHash": "sha256-x"}, "original": {"type": "github", "owner": "x", "repo": "y"}}
    return (json.dumps({"nodes": nodes, "root": "root", "version": 7}, indent=2) + "\n").encode()


OLD = release_lock()
NEW = release_lock(R2, 1792000000, N2, 1791500000, extra_input=True)


class Compare(unittest.TestCase):
    def test_a_newer_release_is_a_nexusos_update_with_the_nixpkgs_it_brings(self):
        change = system_update.compare_update(OLD, NEW)
        self.assertEqual({"source": "github:TheMatthewWalker/NexusOS/release",
                          "before": {"rev": R1, "lastModified": 1791000000},
                          "after": {"rev": R2, "lastModified": 1792000000}}, change["nexusos"])
        self.assertEqual(N2, change["nixpkgs"]["after"]["rev"])
        self.assertEqual("nixos-26.05", change["nixpkgs"]["branch"])
        # A security-updates-only caller refuses it.
        with self.assertRaises(UpdateError):
            system_update.compare(OLD, NEW)

    def test_security_updates_alone_still_work_for_a_release_computer(self):
        newer_nixpkgs = release_lock(nixpkgs=N2, nix_modified=1790500000)
        change = system_update.compare_update(OLD, newer_nixpkgs)
        self.assertNotIn("nexusos", change)
        self.assertEqual(system_update.compare(OLD, newer_nixpkgs), change["nixpkgs"])

    def test_going_back_or_elsewhere_is_refused(self):
        for bad, words in [(release_lock(R2, 1790000000), "older NexusOS"),
                           (release_lock(R2, 1792000000, owner="someone-else"), "where NexusOS comes from"),
                           (release_lock(R2, 1792000000, ref="main"), "where NexusOS comes from"),
                           (release_lock(R2, 1792000000, N2, branch="nixos-unstable"), "NixOS release branch")]:
            with self.subTest(words=words), self.assertRaisesRegex(UpdateError, words):
                system_update.compare_update(OLD, bad)

    def test_a_local_copy_never_moves_by_itself(self):
        local = json.loads(OLD)
        local["nodes"]["nexusos"]["locked"] = local["nodes"]["nexusos"]["original"] = {"type": "path", "path": "./nexusos"}
        moved = json.loads(OLD)
        moved["nodes"]["nexusos"]["locked"] = {"type": "path", "path": "./nexusos", "narHash": "sha256-zz"}
        moved["nodes"]["nexusos"]["original"] = {"type": "path", "path": "./nexusos"}
        with self.assertRaisesRegex(UpdateError, "does not follow a NexusOS release"):
            system_update.compare_update(json.dumps(local).encode(), json.dumps(moved).encode())
        self.assertFalse(follows_release(json.dumps(local).encode()))
        self.assertTrue(follows_release(OLD))

    def test_the_root_of_the_lock_never_changes(self):
        changed = json.loads(NEW)
        changed["nodes"]["root"]["inputs"]["other"] = "extra"
        with self.assertRaisesRegex(UpdateError, "asks for"):
            system_update.compare_update(OLD, json.dumps(changed).encode())


FLAKE = """{
  inputs.nexusos.url = "github:TheMatthewWalker/NexusOS/release";
  outputs = { nexusos, ... }: { };
}
"""


class Channels(unittest.TestCase):
    def test_moving_to_testing_and_back_is_a_forward_nexusos_update(self):
        testing = release_lock(R2, 1792000000, N2, 1791500000, ref="testing")
        change = system_update.compare_update(OLD, testing)
        self.assertEqual("github:TheMatthewWalker/NexusOS/testing", change["nexusos"]["source"])
        self.assertEqual(change["nexusos"], validate_update({"lockHash": "0" * 64, "previousLockHash": "1" * 64,
            "nixpkgs": change["nixpkgs"], "nexusos": change["nexusos"], "packages": [], "otherPackages": 0,
            "added": 0, "removed": 0})["nexusos"])
        back = release_lock("3" * 40, 1793000000, N2, 1791500000)
        self.assertEqual("github:TheMatthewWalker/NexusOS/release", system_update.compare_update(testing, back)["nexusos"]["source"])
        # Back to stable is never a step back in time: it waits until stable is newer.
        with self.assertRaisesRegex(UpdateError, "older NexusOS"):
            system_update.compare_update(testing, release_lock(R1, 1791500000))
        with self.assertRaisesRegex(UpdateError, "where NexusOS comes from"):
            system_update.compare_update(OLD, release_lock(R2, 1792000000, owner="someone-else", ref="testing"))

    def test_flake_nix_follows_the_lock_s_branch(self):
        testing = release_lock(R2, 1792000000, ref="testing")
        synced = system_update.sync_flake(FLAKE, testing)
        self.assertIn('inputs.nexusos.url = "github:TheMatthewWalker/NexusOS/testing";', synced)
        self.assertEqual(synced.replace("/testing", "/release"), FLAKE)
        self.assertEqual(system_update.sync_flake(FLAKE, OLD), FLAKE)
        self.assertEqual(system_update.sync_flake("{ inputs.nexusos.url = \"path:./nexusos\"; }", testing),
                         "{ inputs.nexusos.url = \"path:./nexusos\"; }")
        with self.assertRaises(UpdateError):
            system_update.set_flake_branch(FLAKE, "main")
        with self.assertRaises(UpdateError):
            system_update.set_flake_branch(FLAKE + FLAKE, "testing")

    def test_the_wanted_channel_is_stable_unless_testing_was_chosen(self):
        m = Machine()
        self.addCleanup(m.close)
        self.assertEqual("release", system_update.wanted_channel(m.host))
        (m.host / "nexusos-channel").write_text("testing\n")
        self.assertEqual("testing", system_update.wanted_channel(m.host))
        (m.host / "nexusos-channel").write_text("main\n")
        self.assertEqual("release", system_update.wanted_channel(m.host))

    def test_the_check_follows_the_chosen_channel_and_installing_moves_flake_nix(self):
        m = Machine()
        self.addCleanup(m.close)
        (m.host / "flake.lock").write_bytes(OLD)
        (m.host / "flake.nix").write_text(FLAKE)
        (m.host / "nexusos-channel").write_text("testing\n")
        testing = release_lock(R2, 1792000000, N2, 1791500000, ref="testing")
        seen = []

        def runner(command, timeout):
            if "update" in command and command[command.index("update") + 1] == "nexusos":
                copy = Path(command[command.index("--flake") + 1].removeprefix("path:"))
                seen.append((copy / "flake.nix").read_text())
            return fetcher(testing)(command, timeout)
        record = update_check(m.updates, m.host, runner=runner, free=lambda: 50 * 1024 ** 3, clock=lambda: 1000)
        self.assertEqual("available", record["state"])
        self.assertIn("NexusOS/testing", seen[0])
        self.assertEqual(FLAKE, (m.host / "flake.nix").read_text())          # /etc/nixos untouched until installed
        waiting = system_update.pending(m.updates, m.host, owner=m.uid)
        self.assertEqual("github:TheMatthewWalker/NexusOS/testing", waiting["nexusos"]["source"])
        staged = m.updates / "staged.lock"
        staged.write_bytes(testing)
        self.assertEqual("installed", system_update.install_lock(staged, system_update.sha256(testing),
                                                                 system_update.sha256(OLD), m.host))
        self.assertIn("NexusOS/testing", (m.host / "flake.nix").read_text())
        self.assertEqual(testing, (m.host / "flake.lock").read_bytes())


class Check(unittest.TestCase):
    def setUp(self):
        self.m = Machine()
        self.addCleanup(self.m.close)
        (self.m.host / "flake.lock").write_bytes(OLD)

    def test_nexusos_moves_first_then_nixpkgs_and_it_is_offered_and_signed_as_a_nexusos_update(self):
        commands = []
        record = update_check(self.m.updates, self.m.host, runner=fetcher(NEW, commands), free=lambda: 50 * 1024 ** 3,
                              clock=lambda: 1000)
        self.assertEqual("available", record["state"])
        self.assertIn("NexusOS update", record["message"])
        self.assertEqual(["flake", "update", "nexusos"], commands[0][3:6])
        self.assertEqual(["flake", "update", "nexusos/nixpkgs"], commands[1][3:6])
        waiting = system_update.pending(self.m.updates, self.m.host, owner=self.m.uid)
        self.assertEqual(R2, waiting["nexusos"]["after"]["rev"])
        # What the builder signs passes the attestation's own check.
        signed = {"lockHash": waiting["lockHash"], "previousLockHash": waiting["previousLockHash"], "nixpkgs": waiting["nixpkgs"],
                  "nexusos": waiting["nexusos"], "packages": [], "otherPackages": 0, "added": 0, "removed": 0}
        self.assertEqual(signed, validate_update(signed))
        backwards = {**signed, "nexusos": {**waiting["nexusos"], "after": waiting["nexusos"]["before"]}}
        with self.assertRaises(ValueError):
            validate_update(backwards)

    def test_a_local_copy_computer_only_gets_security_updates(self):
        local = json.loads(OLD)
        local["nodes"]["nexusos"]["locked"] = local["nodes"]["nexusos"]["original"] = {"type": "path", "path": "./nexusos"}
        (self.m.host / "flake.lock").write_bytes(json.dumps(local).encode())
        commands = []
        update_check(self.m.updates, self.m.host, runner=fetcher(json.dumps(local).encode(), commands),
                     free=lambda: 50 * 1024 ** 3, clock=lambda: 1000)
        self.assertEqual(["flake", "update", "nexusos/nixpkgs"], commands[0][3:6])
        self.assertFalse(any(c[3:6] == ["flake", "update", "nexusos"] and len(c) > 5 and c[5] == "nexusos" for c in commands))


if __name__ == "__main__":
    unittest.main()
