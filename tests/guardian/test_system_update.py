"""Security updates as Guardian changes (system_update.py, update_check.py): the update check, the
"update" candidate kind through builder, broker and describe, staging, and the boot check that
copies the lock into /etc/nixos only after a confirmed trial. No nix or root: fake runners, test UID."""
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest

from support import BASELINE, CHANGES, DRV, HOST, TOPLEVEL, TRIAL, Observer, statement
import system_update
from attestation import load_trusted_keys, sign, verify
from boot_check import check as boot_check
from candidate import prepare, prepare_update
from local_builder import LocalBuilder, init_keys, load_private_key
from recovery_gate import eligible
from root_review_broker import Denied, GuardianBroker
from stage_executor import StagingError, stage
from system_update import UpdateError
from update_check import check as update_check

OLD_REV, NEW_REV = "a" * 40, "b" * 40
VM_DRV = "/nix/store/" + "8" * 32 + "-nexus-guardian-vm-test.drv"
DIFF = """linux: 6.18.56 → 6.18.60, +12.3 KiB
firefox: 144.0 → 144.0.2, \x1b[31m+1024.0 KiB\x1b[0m
firefox-unwrapped: 144.0 → 144.0.2, +1024.0 KiB
chromium: 141.0.7390.65 → 141.0.7390.107
zlib: 1.3.1 → 1.3.2
glibc: 2.40-66 → 2.40-218
hello: ∅ → 2.12.1, +100.0 KiB
oldthing: 1.0 → ∅, -10.0 KiB
mesa: +1.2 KiB
"""


def lock(rev=OLD_REV, modified=1791765022, ref="nixos-26.05", extra=None):
    nodes = {
        "nexusos": {"inputs": {"nixpkgs": "nixpkgs"}, "locked": {"path": "./nexusos", "type": "path"},
                    "original": {"path": "./nexusos", "type": "path"}, "parent": []},
        "nixpkgs": {"locked": {"lastModified": modified, "narHash": "sha256-" + rev[:8], "owner": "NixOS",
                               "repo": "nixpkgs", "rev": rev, "type": "github"},
                    "original": {"owner": "NixOS", "ref": ref, "repo": "nixpkgs", "type": "github"}},
        "root": {"inputs": {"nexusos": "nexusos"}},
    }
    nodes.update(extra or {})
    return (json.dumps({"nodes": nodes, "root": "root", "version": 7}, indent=2) + "\n").encode()


OLD, NEW = lock(), lock(NEW_REV, 1792900000)
SOURCE = "/nix/store/" + "5" * 32 + "-source"


def fetcher(new, commands=None):
    """nix as the update check uses it: flake update writes the new lock; archive names the fetched source."""
    def runner(command, timeout):
        if commands is not None:
            commands.append(command)
        if "update" in command:
            (Path(command[command.index("--flake") + 1].removeprefix("path:")) / "flake.lock").write_bytes(new)
        if "archive" in command:
            return 0, json.dumps({"path": "/nix/store/x", "inputs": {"nexusos": {"inputs": {"nixpkgs": {"path": SOURCE}}}}}), ""
        return 0, "", ""
    return runner


class Machine:
    """/etc/nixos, /var/lib/nexus-updates and Guardian's state, in a temp dir owned by the test UID."""

    def __init__(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.uid = os.geteuid()
        self.host = root / "etc-nixos"
        (self.host / "nexusos").mkdir(parents=True)
        (self.host / "flake.nix").write_text("{ }\n")
        (self.host / "nexusos" / "flake.nix").write_text("{ }\n")
        (self.host / "flake.lock").write_bytes(OLD)
        self.updates = root / "updates"
        self.state = root / "state"
        self.state.mkdir(mode=0o700)
        self.builder_dir = root / "builder"

    def find_update(self, new=NEW):
        return update_check(self.updates, self.host, runner=fetcher(new), free=lambda: 50 * 1024 ** 3, clock=lambda: 1000)

    def broker(self, key_file, observer=None):
        return GuardianBroker(self.state, key_file, {self.uid}, HOST, owner=self.uid, observer=observer or Observer(),
                              store_checker=lambda _: True, updates_dir=self.updates, host_flake_dir=self.host)

    def close(self):
        self.temp.cleanup()


class LockTests(unittest.TestCase):
    def test_only_nixpkgs_moving_forward_on_its_branch_is_an_update(self):
        summary = system_update.compare(OLD, NEW)
        self.assertEqual({"branch": "nixos-26.05", "before": {"rev": OLD_REV, "lastModified": 1791765022},
                          "after": {"rev": NEW_REV, "lastModified": 1792900000}}, summary)
        self.assertIsNone(system_update.compare(OLD, OLD))

    def test_anything_else_is_refused(self):
        other = {"evil": {"locked": {"type": "path", "path": "/tmp/x"}, "original": {"type": "path", "path": "/tmp/x"}}}
        for new in (lock(NEW_REV, 1792900000, ref="nixos-unstable"),     # another branch
                    lock(NEW_REV, 1700000000),                           # older
                    lock(NEW_REV, 1792900000, extra=other),              # a new input
                    NEW.replace(b'"./nexusos"', b'"./elsewhere"'),       # NexusOS's own source moved
                    NEW.replace(b'"owner": "NixOS"', b'"owner": "Mallory"', 1),
                    b"not json"):
            with self.subTest(new=new[:60]), self.assertRaises(UpdateError):
                system_update.compare(OLD, new)

    def test_diff_closures_is_summarised_notable_first(self):
        summary = system_update.diff_summary(DIFF)
        names = [p["name"] for p in summary["packages"]]
        self.assertEqual(["linux", "firefox", "chromium", "glibc", "zlib"], names)  # firefox-unwrapped folded in
        self.assertEqual({"name": "linux", "before": "6.18.56", "after": "6.18.60"}, summary["packages"][0])
        self.assertEqual((0, 1, 1), (summary["otherPackages"], summary["added"], summary["removed"]))
        many = "".join(f"pkg{i}: 1.{i} → 2.{i}\n" for i in range(30))
        summary = system_update.diff_summary(many)
        self.assertEqual((system_update.MAX_LISTED, 30 - system_update.MAX_LISTED),
                         (len(summary["packages"]), summary["otherPackages"]))


class UpdateCheckTests(unittest.TestCase):
    def setUp(self):
        self.m = Machine()
        self.addCleanup(self.m.close)

    def test_a_newer_nixpkgs_is_recorded_by_hash_and_etc_nixos_is_untouched(self):
        commands = []
        record = update_check(self.m.updates, self.m.host, runner=fetcher(NEW, commands), free=lambda: 50 * 1024 ** 3,
                              clock=lambda: 1000)
        self.assertEqual("available", record["state"])
        self.assertEqual(["flake", "update", "nexusos/nixpkgs"], commands[0][3:6])
        # The fetched nixpkgs is kept from garbage collection until it is built (the builder has no network).
        lock_hash = system_update.sha256(NEW)
        self.assertEqual(["build", "--out-link", str(self.m.updates / "roots" / lock_hash), SOURCE], commands[-1][3:])
        self.assertEqual(OLD, (self.m.host / "flake.lock").read_bytes())          # never changed here
        self.assertFalse((self.m.updates / "work").exists())                       # work dir removed
        waiting = system_update.pending(self.m.updates, self.m.host, owner=self.m.uid)
        self.assertEqual(system_update.sha256(NEW), waiting["lockHash"])
        self.assertEqual(system_update.sha256(OLD), waiting["previousLockHash"])
        self.assertEqual(NEW_REV, waiting["nixpkgs"]["after"]["rev"])
        self.assertEqual(0o700, os.stat(self.m.updates).st_mode & 0o777)

    def test_nothing_new_is_up_to_date(self):
        self.assertEqual("up-to-date", self.m.find_update(OLD)["state"])
        self.assertIsNone(system_update.pending(self.m.updates, self.m.host, owner=self.m.uid))

    def test_low_disk_is_reported_and_nothing_is_fetched(self):
        record = update_check(self.m.updates, self.m.host, runner=lambda *_: self.fail("fetched"),
                              free=lambda: 3 * 1024 ** 3, clock=lambda: 1000)
        self.assertEqual("low-disk", record["state"])
        self.assertIn("3.0 GiB free, 10.0 GiB needed", record["message"])
        self.assertEqual("low-disk", system_update.status(self.m.updates, owner=self.m.uid)["state"])

    def test_offline_and_unexpected_locks_are_not_offered(self):
        self.assertEqual("offline", update_check(self.m.updates, self.m.host, online=lambda: False,
                                                 free=lambda: 50 * 1024 ** 3)["state"])
        self.assertEqual("failed", self.m.find_update(lock(NEW_REV, 1792900000, ref="nixos-unstable"))["state"])
        self.assertIsNone(system_update.pending(self.m.updates, self.m.host, owner=self.m.uid))

    def test_an_update_is_offered_only_while_etc_nixos_has_its_old_lock(self):
        self.m.find_update()
        (self.m.host / "flake.lock").write_bytes(OLD.replace(b"version", b"version", 1) + b"\n")
        self.assertIsNone(system_update.pending(self.m.updates, self.m.host, owner=self.m.uid))

    def test_a_tampered_lock_is_refused(self):
        self.m.find_update()
        path = self.m.updates / "locks" / f"{system_update.sha256(NEW)}.lock"
        path.write_bytes(NEW.replace(NEW_REV.encode(), b"c" * 40))
        with self.assertRaises(UpdateError):
            system_update.pending(self.m.updates, self.m.host, owner=self.m.uid)
        os.chmod(path, 0o666)
        with self.assertRaises(UpdateError):
            system_update.read_lock(self.m.updates, system_update.sha256(NEW), owner=self.m.uid)

    def test_locks_are_read_only_by_hash_from_the_update_directory(self):
        self.m.find_update()
        for name in ("../../etc-nixos/flake", "/etc/passwd", "A" * 64, 7):
            with self.subTest(name=name), self.assertRaises(UpdateError):
                system_update.read_lock(self.m.updates, name, owner=self.m.uid)


class UpdateCandidateTests(unittest.TestCase):
    def test_the_candidate_is_bound_to_the_lock_hash(self):
        a = prepare_update("1" * 64, BASELINE, HOST)
        self.assertEqual("update", a["kind"])
        self.assertNotIn("nixModule", a)
        self.assertNotEqual(a["candidateId"], prepare_update("2" * 64, BASELINE, HOST)["candidateId"])
        self.assertNotEqual(a["candidateId"], prepare(CHANGES, BASELINE, HOST)["candidateId"])
        for bad in ("../flake.lock", "1" * 63, None):
            with self.assertRaises(ValueError):
                prepare_update(bad, BASELINE, HOST)

    def test_eligibility_needs_a_statement_of_that_very_lock(self):
        candidate = prepare_update("1" * 64, BASELINE, HOST)
        update = {"lockHash": "1" * 64, "previousLockHash": "0" * 64}
        signed = {"lockHash": "1" * 64}
        self.assertTrue(eligible(candidate, None, statement(candidate, update=signed), BASELINE, update=update))
        self.assertFalse(eligible(candidate, None, statement(candidate, update={"lockHash": "2" * 64}), BASELINE, update=update))
        self.assertFalse(eligible(candidate, None, statement(candidate), BASELINE, update=update))
        change = prepare(CHANGES, BASELINE, HOST)
        self.assertFalse(eligible(change, CHANGES, statement(change, update=signed), BASELINE))
        with self.assertRaises(ValueError):
            eligible(candidate, None, statement(candidate, update=signed), BASELINE, update={"lockHash": "2" * 64})


class BuilderAndBrokerTests(unittest.TestCase):
    """The whole path of one update: builder, broker describe and review, staging, boot check."""

    def setUp(self):
        self.m = Machine()
        self.addCleanup(self.m.close)
        self.m.find_update()
        self.lock_hash = system_update.sha256(NEW)
        init_keys(self.m.builder_dir)
        self.commands, self.seen_lock = [], None
        self.free = 50 * 1024 ** 3
        self.builder = LocalBuilder(self.m.builder_dir, load_private_key(self.m.builder_dir, owner=self.m.uid), {self.m.uid},
                                    HOST, observer=Observer(), runner=self.nix, owner=self.m.uid, start_worker=False,
                                    updates_dir=self.m.updates, host_flake_dir=self.m.host, free=lambda: self.free)
        self.broker = self.m.broker(self.m.builder_dir / "trusted-keys")

    def nix(self, command, timeout):
        self.commands.append(command)
        if "eval" in command:
            flake = command[command.index("flake") + 1]
            self.seen_lock = (Path(flake.removeprefix("path:")) / "flake.lock").read_bytes()
            return 0, json.dumps({"toplevel": DRV, "vmTest": VM_DRV, "before": None}), ""
        if "diff-closures" in command:
            return 0, DIFF, ""
        if command[-1] == DRV + "^out":
            return 0, TOPLEVEL + "\n", ""
        return 0, "", ""

    def built(self):
        result = self.builder.build(self.m.uid, {"update": {"lockHash": self.lock_hash}})
        candidate_id = result["candidate"]["candidateId"]
        self.builder.run_job(candidate_id)
        return result["candidate"], self.builder.status(self.m.uid, {"candidateId": candidate_id})

    def test_built_from_a_copy_of_etc_nixos_with_the_root_owned_lock_and_signed(self):
        candidate, job = self.built()
        self.assertEqual("passed", job["state"], job["message"])
        flake = self.commands[0][self.commands[0].index("flake") + 1]
        folder = self.m.builder_dir / "work" / "results" / candidate["candidateId"]
        self.assertEqual(f"path:{folder / 'flake'}", flake)
        self.assertEqual(NEW, self.seen_lock)
        self.assertFalse((folder / "flake").exists())                 # removed after evaluation
        self.assertEqual("", self.commands[0][self.commands[0].index("module") + 1])  # no overlay
        self.assertEqual(["store", "diff-closures", BASELINE, TOPLEVEL], self.commands[-1][3:])
        statement_ = verify(job["attestation"], load_trusted_keys(self.m.builder_dir / "trusted-keys", owner=self.m.uid))
        self.assertEqual(self.lock_hash, statement_["update"]["lockHash"])
        self.assertEqual(system_update.sha256(OLD), statement_["update"]["previousLockHash"])
        self.assertEqual("6.18.60", statement_["update"]["packages"][0]["after"])
        self.assertEqual("update", job["kind"])
        self.assertEqual(statement_["update"], job["update"])

    def test_the_builder_takes_only_the_waiting_lock_never_a_path(self):
        for payload in ({"update": {"lockHash": "f" * 64}}, {"update": {"lockHash": self.lock_hash, "path": "/tmp/x"}},
                        {"update": {"lock": "/home/me/flake.lock"}}, {"update": "/tmp/flake.lock"}):
            with self.subTest(payload=payload), self.assertRaises(Denied):
                self.builder.build(self.m.uid, payload)

    def test_the_waiting_update_is_prepared_before_anyone_asks_and_joined_when_asked(self):
        candidate_id = self.builder.prepare_waiting_update()
        self.assertIsNotNone(candidate_id)
        # Nobody asked: nobody can see it yet, and it is not queued twice.
        self.assertIsNone(self.builder.prepare_waiting_update())
        with self.assertRaises(Denied):
            self.builder.status(self.m.uid, {"candidateId": candidate_id})
        self.builder.run_job(candidate_id)
        built = len(self.commands)
        # The person's request joins the finished job: nothing is built again.
        result = self.builder.build(self.m.uid, {"update": {"lockHash": self.lock_hash}})
        self.assertEqual(candidate_id, result["candidate"]["candidateId"])
        self.assertEqual("passed", result["job"]["state"], result["job"]["message"])
        self.assertEqual(built, len(self.commands))
        self.assertEqual("passed", self.builder.status(self.m.uid, {"candidateId": candidate_id})["state"])

    def test_nothing_is_prepared_without_an_update_or_room(self):
        self.free = 2 * 1024 ** 3
        self.assertIsNone(self.builder.prepare_waiting_update())
        self.free = 50 * 1024 ** 3
        (self.m.host / "flake.lock").write_bytes(OLD + b"\n")       # /etc/nixos changed: no update waiting
        self.assertIsNone(self.builder.prepare_waiting_update())

    def test_low_disk_refuses_to_build(self):
        self.free = 2 * 1024 ** 3
        with self.assertRaisesRegex(Denied, "Not enough free disk space"):
            self.builder.build(self.m.uid, {"update": {"lockHash": self.lock_hash}})

    def test_a_changed_etc_nixos_stops_the_build(self):
        result = self.builder.build(self.m.uid, {"update": {"lockHash": self.lock_hash}})
        (self.m.host / "flake.lock").write_bytes(OLD + b"\n")
        self.builder.run_job(result["candidate"]["candidateId"])
        job = self.builder.status(self.m.uid, {"candidateId": result["candidate"]["candidateId"]})
        self.assertEqual("failed", job["state"])
        self.assertEqual([], self.commands)

    def test_updates_and_check_requests(self):
        view = self.builder.updates(self.m.uid, {})
        self.assertEqual(self.lock_hash, view["available"]["lockHash"])
        self.assertEqual("available", view["status"]["state"])
        self.assertTrue(self.builder.check_updates(self.m.uid, {})["requested"])
        self.assertTrue((self.m.builder_dir / "work" / "update-requested").exists())
        self.assertFalse(self.builder.check_updates(self.m.uid, {})["requested"])  # not again within minutes
        with self.assertRaises(Denied):
            self.builder.handle(self.m.uid + 1, {"operation": "check-updates", "payload": {}})

    def approved(self):
        candidate, job = self.built()
        submitted = self.broker.submit(self.m.uid, {"description": "Security updates", "update": {"lockHash": self.lock_hash}})
        self.assertEqual(candidate, submitted["candidate"])
        record = self.broker.describe(self.m.uid, {"candidateId": candidate["candidateId"], "attestation": job["attestation"]})
        self.broker.register_approval(candidate["candidateId"], self.m.uid, build=record["buildHash"])
        decision = self.broker.review(self.m.uid, {"candidateId": candidate["candidateId"], "attestation": job["attestation"]})
        return record, decision

    def test_describe_names_the_update_precisely(self):
        record, decision = self.approved()
        self.assertTrue(decision["eligible"])
        self.assertEqual("update", record["kind"])
        self.assertEqual(([], []), (record["settings"], record["packages"]))
        update = record["update"]
        self.assertEqual((OLD_REV, NEW_REV), (update["nixpkgs"]["before"]["rev"], update["nixpkgs"]["after"]["rev"]))
        self.assertEqual("nixos-26.05", update["nixpkgs"]["branch"])
        self.assertEqual({"name": "linux", "label": "Linux", "before": "6.18.56", "after": "6.18.60"}, update["packages"][0])
        self.assertEqual("Firefox", update["packages"][1]["label"])
        self.assertEqual(self.lock_hash, update["lockHash"])

    def test_the_broker_refuses_an_update_that_is_not_waiting(self):
        with self.assertRaises(Denied):
            self.broker.submit(self.m.uid, {"description": "x", "update": {"lockHash": "e" * 64}})
        with self.assertRaises(Denied):
            self.broker.submit(self.m.uid, {"description": "x", "update": {"lockHash": self.lock_hash}, "changes": CHANGES})

    def test_an_attestation_of_another_lock_is_not_eligible(self):
        candidate, job = self.built()
        self.broker.submit(self.m.uid, {"description": "Security updates", "update": {"lockHash": self.lock_hash}})
        key = load_private_key(self.m.builder_dir, owner=self.m.uid)
        forged = dict(job["attestation"]["statement"])
        forged["update"] = {**forged["update"], "lockHash": "f" * 64}
        with self.assertRaises(Denied):
            self.broker.describe(self.m.uid, {"candidateId": candidate["candidateId"], "attestation": sign(forged, key)})
        self.broker.register_approval(candidate["candidateId"], self.m.uid)
        review = self.broker.review(self.m.uid, {"candidateId": candidate["candidateId"], "attestation": sign(forged, key)})
        self.assertFalse(review["eligible"])

    # --- staging and the boot after ---

    def staged(self):
        _, decision = self.approved()
        isfile = os.path.isfile
        os.path.isfile = lambda path: path == os.path.join(TOPLEVEL, "bin", "switch-to-configuration") or isfile(path)
        self.addCleanup(lambda: setattr(os.path, "isfile", isfile))
        cfg = Path(self.m.temp.name) / "grub.cfg"
        cfg.write_text("")
        commands = []
        import stage_executor
        original = stage_executor._grub_trial
        stage_executor._grub_trial = lambda *a: ("1", "2>1")
        self.addCleanup(lambda: setattr(stage_executor, "_grub_trial", original))
        return stage(self.broker, decision["decision"], runner=commands.append, observer=Observer(),
                     trial_of=lambda toplevel: TRIAL, updates_dir=self.m.updates)

    def test_staging_keeps_the_built_lock_with_the_trial(self):
        state = self.staged()
        staged = self.m.state / "staged-flake.lock"
        self.assertEqual(NEW, staged.read_bytes())
        self.assertEqual({"path": str(staged), "sha256": self.lock_hash, "previous": system_update.sha256(OLD)},
                         state["flakeLock"])
        self.assertEqual(OLD, (self.m.host / "flake.lock").read_bytes())   # not before the trial boot

    def boot(self, current, healthy=True):
        return boot_check(self.m.state, current=current, health=lambda _: (healthy, "test"), runner=lambda _: None,
                          grubenv="/tmp/grubenv", host_flake_dir=self.m.host)

    def test_a_confirmed_trial_copies_the_lock_into_etc_nixos(self):
        self.staged()
        self.assertEqual("confirmed", self.boot(TRIAL))
        self.assertEqual(NEW, (self.m.host / "flake.lock").read_bytes())
        self.assertEqual(0o644, os.stat(self.m.host / "flake.lock").st_mode & 0o777)
        self.assertFalse((self.m.state / "staged-flake.lock").exists())
        self.assertIn('"flake-lock-installed"', (self.m.state / "staging.jsonl").read_text())
        # Installed: no longer offered, and the next check starts from it.
        self.assertIsNone(system_update.pending(self.m.updates, self.m.host, owner=self.m.uid))

    def test_fallback_and_rollback_leave_etc_nixos_as_it_was(self):
        for current, healthy, outcome in ((BASELINE, True, "fell-back"), (TRIAL, False, "rolled-back")):
            with self.subTest(outcome=outcome):
                self.setUp()
                self.staged()
                self.assertEqual(outcome, self.boot(current, healthy))
                self.assertEqual(OLD, (self.m.host / "flake.lock").read_bytes())
                self.assertFalse((self.m.state / "staged-flake.lock").exists())

    def test_a_lock_changed_meanwhile_is_left_alone(self):
        self.staged()
        (self.m.host / "flake.lock").write_bytes(b"{}\n")
        self.assertEqual("confirmed", self.boot(TRIAL))
        self.assertEqual(b"{}\n", (self.m.host / "flake.lock").read_bytes())
        self.assertIn('"flake-lock-skipped"', (self.m.state / "staging.jsonl").read_text())

    def test_staging_refuses_when_the_lock_is_gone(self):
        _, decision = self.approved()
        shutil.rmtree(self.m.updates / "locks")
        isfile = os.path.isfile
        os.path.isfile = lambda path: path == os.path.join(TOPLEVEL, "bin", "switch-to-configuration") or isfile(path)
        self.addCleanup(lambda: setattr(os.path, "isfile", isfile))
        commands = []
        with self.assertRaises(StagingError):
            stage(self.broker, decision["decision"], runner=commands.append, observer=Observer(),
                  trial_of=lambda toplevel: TRIAL, updates_dir=self.m.updates)
        self.assertEqual([], commands)


if __name__ == "__main__":
    unittest.main()
