"""Guardian on an installed computer: systemd-boot trial boots, the local builder and its
key and the staging service (desktop approval: test_desktop_approve.py). No nix, bootctl or root needed:
commands go to fake runners and services are owned by the test UID."""
import base64
import json
import os
from pathlib import Path
import tempfile
import unittest

from support import BASELINE, CHANGES, DRV, TOPLEVEL, TRIAL, BrokerFixture
import systemd_boot
from attestation import load_trusted_keys, verify
from boot_check import check
from local_builder import LocalBuilder, init_keys, load_private_key
from root_review_broker import Denied
from stage_executor import PROFILE, StagingError, stage
from stage_service import StageService

OTHER = "/nix/store/" + "6" * 32 + "-nixos-system-lab-26.05"
VM_DRV = "/nix/store/" + "8" * 32 + "-nexus-guardian-vm-test.drv"


def write_entries(directory, generations, specialisation=None, trials=None):
    """trials maps a generation number to its trial-boot specialisation (guardian-trial)."""
    entries = Path(directory) / "loader" / "entries"
    entries.mkdir(parents=True, exist_ok=True)
    for number, path in generations:
        (entries / f"nixos-generation-{number}.conf").write_text(
            f"title NexusOS\nversion Generation {number}\nlinux /EFI/nixos/k-bzImage.efi\n"
            f"initrd /EFI/nixos/i-initrd.efi\noptions init={path}/init loglevel=4\n")
    for number, path in (trials or {}).items():
        (entries / f"nixos-generation-{number}-specialisation-guardian-trial.conf").write_text(
            f"options init={path}/init loglevel=4 panic=10 boot.panic_on_fail\n")
    if specialisation:
        number, path = specialisation
        (entries / f"nixos-generation-{number}-specialisation-x.conf").write_text(f"options init={path}/init\n")
    return entries


class Runner:
    def __init__(self, fail_on=None):
        self.commands, self.fail_on = [], fail_on

    def __call__(self, command):
        self.commands.append(command)
        if self.fail_on and self.fail_on(command):
            raise OSError("simulated failure")


class SystemdBootEntryTests(unittest.TestCase):
    def test_finds_newest_plain_generation_by_exact_init(self):
        with tempfile.TemporaryDirectory() as esp:
            entries = write_entries(esp, [(1, BASELINE), (2, TOPLEVEL), (3, BASELINE)], specialisation=(4, OTHER))
            self.assertEqual("nixos-generation-2.conf", systemd_boot.entry_for(entries, TOPLEVEL))
            self.assertEqual("nixos-generation-3.conf", systemd_boot.entry_for(entries, BASELINE))
            self.assertIsNone(systemd_boot.entry_for(entries, OTHER))  # Specialisations are never chosen.
            self.assertIsNone(systemd_boot.entry_for(entries, BASELINE[:-2]))  # No prefix matches.
            self.assertIsNone(systemd_boot.entry_for(Path(esp) / "missing", BASELINE))

    def test_finds_the_trial_boot_entry_only_by_its_specialisation(self):
        with tempfile.TemporaryDirectory() as esp:
            entries = write_entries(esp, [(1, BASELINE), (2, TOPLEVEL)], specialisation=(2, OTHER), trials={2: TRIAL})
            self.assertEqual("nixos-generation-2-specialisation-guardian-trial.conf",
                             systemd_boot.entry_for(entries, TRIAL, "guardian-trial"))
            self.assertIsNone(systemd_boot.entry_for(entries, TRIAL))  # Never as a plain generation.
            self.assertIsNone(systemd_boot.entry_for(entries, OTHER, "guardian-trial"))
            self.assertEqual("nixos-generation-2.conf", systemd_boot.entry_for(entries, TOPLEVEL))


class SystemdBootStagingTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture()
        self.addCleanup(self.fx.close)
        candidate = self.fx.submitted()
        self.fx.broker.register_approval(candidate["candidateId"], self.fx.uid)
        self.decision = self.fx.broker.review(self.fx.uid, {"candidateId": candidate["candidateId"],
                                                            "attestation": self.fx.attest(candidate)})["decision"]
        self.esp = tempfile.TemporaryDirectory()
        self.addCleanup(self.esp.cleanup)
        self.isfile = os.path.isfile
        os.path.isfile = lambda path: path == os.path.join(TOPLEVEL, "bin", "switch-to-configuration") or self.isfile(path)
        self.addCleanup(lambda: setattr(os.path, "isfile", self.isfile))

    def stage(self, runner):
        return stage(self.fx.broker, self.decision, runner=runner, observer=self.fx.observer,
                     bootloader="systemd-boot", esp=self.esp.name,
                     trial_of=lambda toplevel: TRIAL if toplevel == TOPLEVEL else None)

    def test_one_shot_candidate_with_baseline_as_efi_default(self):
        write_entries(self.esp.name, [(1, BASELINE), (2, TOPLEVEL)], trials={2: TRIAL})
        runner = Runner()
        state = self.stage(runner)
        # One shot of the trial-boot entry; the plain entries never get panic=N boot.panic_on_fail.
        self.assertEqual([["nix-env", "--profile", PROFILE, "--set", TOPLEVEL],
                          [TOPLEVEL + "/bin/switch-to-configuration", "boot"],
                          ["bootctl", "set-default", "nixos-generation-1.conf"],
                          ["bootctl", "set-oneshot", "nixos-generation-2-specialisation-guardian-trial.conf"]],
                         runner.commands)
        pending = json.loads((Path(self.fx.state) / "pending-boot.json").read_text())
        self.assertEqual("systemd-boot", pending["bootloader"])
        self.assertEqual(TRIAL, pending["trialSystem"])
        self.assertEqual("systemd-boot", state["bootloader"])

    def test_missing_entry_restores_baseline_and_clears_efi_overrides(self):
        write_entries(self.esp.name, [(1, BASELINE)])
        runner = Runner()
        with self.assertRaises(StagingError):
            self.stage(runner)
        self.assertEqual([["nix-env", "--profile", PROFILE, "--set", BASELINE],
                          [BASELINE + "/bin/switch-to-configuration", "boot"],
                          ["bootctl", "set-oneshot", ""], ["bootctl", "set-default", ""]], runner.commands[2:])
        self.assertFalse((Path(self.fx.state) / "pending-boot.json").exists())

    def test_missing_trial_boot_entry_restores_baseline(self):
        write_entries(self.esp.name, [(1, BASELINE), (2, TOPLEVEL)])
        runner = Runner()
        with self.assertRaises(StagingError):
            self.stage(runner)
        self.assertIn(["nix-env", "--profile", PROFILE, "--set", BASELINE], runner.commands)
        self.assertNotIn(["bootctl", "set-oneshot", "nixos-generation-2.conf"], runner.commands)

    def test_firmware_refusing_the_one_shot_restores_baseline(self):
        write_entries(self.esp.name, [(1, BASELINE), (2, TOPLEVEL)], trials={2: TRIAL})
        runner = Runner(fail_on=lambda command: command[:2] == ["bootctl", "set-oneshot"] and command[2] != "")
        with self.assertRaises(OSError):
            self.stage(runner)
        self.assertIn(["nix-env", "--profile", PROFILE, "--set", BASELINE], runner.commands)
        self.assertEqual(["bootctl", "set-default", ""], runner.commands[-1])


class SystemdBootCheckTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.state = Path(self.dir.name)
        (self.state / "pending-boot.json").write_text(json.dumps(
            {"decision": "d" * 64, "toplevel": TOPLEVEL, "trialSystem": TRIAL, "baseline": BASELINE,
             "bootloader": "systemd-boot", "trialEntry": "nixos-generation-2-specialisation-guardian-trial.conf",
             "fallbackEntry": "nixos-generation-1.conf"}))

    def run_check(self, current, healthy=True):
        runner = Runner()
        return check(self.state, current=current, health=lambda _: (healthy, "test"), runner=runner), runner.commands

    def test_healthy_trial_drops_the_efi_default(self):
        self.assertEqual(("confirmed", [["bootctl", "set-default", ""]]), self.run_check(TRIAL))

    def test_failed_trial_restores_baseline(self):
        outcome, commands = self.run_check(BASELINE)
        self.assertEqual("fell-back", outcome)
        self.assertEqual([["nix-env", "--profile", PROFILE, "--set", BASELINE],
                          [BASELINE + "/bin/switch-to-configuration", "boot"],
                          ["bootctl", "set-default", ""]], commands)

    def test_unhealthy_trial_rolls_back(self):
        outcome, commands = self.run_check(TRIAL, healthy=False)
        self.assertEqual("rolled-back", outcome)
        self.assertEqual(["nix-env", "--profile", PROFILE, "--set", BASELINE], commands[0])


class BuilderKeyTests(unittest.TestCase):
    def test_key_made_once_root_only_and_trusted(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / "builder"
            static = Path(directory) / "static"
            static.write_text("# configured\n" + base64.b64encode(b"\x01" * 32).decode() + "\n")
            public = init_keys(folder, static)
            self.assertEqual(0o600, (folder / "builder.key").stat().st_mode & 0o777)
            self.assertEqual(0o700, folder.stat().st_mode & 0o777)
            self.assertEqual(0o700, (folder / "work").stat().st_mode & 0o777)
            keys = load_trusted_keys(folder / "trusted-keys", owner=os.geteuid())
            self.assertEqual(2, len(keys))
            self.assertIn(base64.b64decode(public), keys.values())
            # Every boot rewrites the trusted keys but never replaces the private key.
            key_bytes = (folder / "builder.key").read_bytes()
            self.assertEqual(public, init_keys(folder, None))
            self.assertEqual(key_bytes, (folder / "builder.key").read_bytes())
            self.assertEqual(1, len(load_trusted_keys(folder / "trusted-keys", owner=os.geteuid())))

    def test_unsafe_key_permissions_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            init_keys(directory)
            os.chmod(Path(directory) / "builder.key", 0o644)
            with self.assertRaises(PermissionError):
                load_private_key(directory, owner=os.geteuid())


class NixRunner:
    """Answers the builder's nix commands like a working machine (or fails one step)."""

    def __init__(self, fail_step=None, error="error: simulated"):
        self.commands, self.fail_step, self.error = [], fail_step, error

    def __call__(self, command, timeout):
        self.commands.append(command)
        step = "evaluate" if "eval" in command else "vm-test" if command[-1] == VM_DRV + "^out" else "build"
        if step == self.fail_step:
            return 1, "", self.error
        evaluated = {"toplevel": DRV, "vmTest": VM_DRV,
                     "before": {"options": {"services.printing.enable": False, "imports": True}, "packages": {}}}
        return 0, {"evaluate": json.dumps(evaluated), "build": TOPLEVEL + "\n", "vm-test": ""}[step], ""


class LocalBuilderTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture()
        self.addCleanup(self.fx.close)
        self.dir = Path(self.fx.dir) / "builder"
        init_keys(self.dir)
        # The broker trusts this computer's builder, as nexus-guardian-keys arranges at boot.
        self.fx.broker.trusted_keys = load_trusted_keys(self.dir / "trusted-keys", owner=os.geteuid())

    def builder(self, runner, observer=None):
        return LocalBuilder(self.dir, load_private_key(self.dir, owner=os.geteuid()), {self.fx.uid},
                            "path:/etc/nixos#nixosConfigurations.lab", required_units=["nexus-guardian.service"],
                            observer=observer or self.fx.observer, runner=runner, owner=os.geteuid(),
                            start_worker=False)

    def test_builds_tests_and_signs_what_the_broker_accepts(self):
        runner = NixRunner()
        builder = self.builder(runner)
        started = builder.build(self.fx.uid, {"changes": CHANGES})
        candidate_id = started["candidate"]["candidateId"]
        self.assertEqual("queued", started["job"]["state"])
        builder.run_job(candidate_id)
        job = builder.status(self.fx.uid, {"candidateId": candidate_id})
        self.assertEqual("passed", job["state"])
        self.assertEqual({"evaluate": "pass", "build": "pass", "vm-test": "pass"}, job["checks"])
        # Today's values, only for what changes.
        self.assertEqual({"options": {"services.printing.enable": False}, "packages": {}}, job["before"])
        statement = verify(job["attestation"], self.fx.broker.trusted_keys)
        self.assertEqual((TOPLEVEL, DRV, BASELINE), (statement["toplevel"], statement["drvPath"], statement["baseline"]))
        self.assertEqual(job["before"], statement["before"])     # signed, for the approval dialog
        # One evaluation of the packaged file with checked string arguments, then two builds of its results.
        evaluate, build, test = runner.commands
        self.assertIn("--impure", evaluate)
        self.assertEqual("nexus-guardian.service", evaluate[evaluate.index("requiredUnits") + 1])
        self.assertEqual("services.printing.enable", evaluate[evaluate.index("options") + 1])
        self.assertEqual(DRV + "^out", build[-1])
        self.assertEqual(VM_DRV + "^out", test[-1])
        for command in runner.commands:
            self.assertNotIn("switch-to-configuration", " ".join(command))
        module = Path(evaluate[evaluate.index("module") + 1])
        self.assertEqual(started["candidate"]["nixModule"], module.read_text())
        # The same candidate submitted to the broker, approved and reviewed with it, is eligible.
        broker_candidate = self.fx.submitted()
        self.assertEqual(candidate_id, broker_candidate["candidateId"])
        self.fx.broker.register_approval(candidate_id, self.fx.uid)
        decision = self.fx.broker.review(self.fx.uid, {"candidateId": candidate_id, "attestation": job["attestation"]})
        self.assertTrue(decision["eligible"])

    def test_a_failed_step_signs_nothing(self):
        for step in ("evaluate", "build", "vm-test"):
            with self.subTest(step=step):
                builder = self.builder(NixRunner(fail_step=step))
                candidate_id = builder.build(self.fx.uid, {"changes": CHANGES})["candidate"]["candidateId"]
                builder.run_job(candidate_id)
                job = builder.status(self.fx.uid, {"candidateId": candidate_id})
                self.assertEqual(("failed", "fail", None), (job["state"], job["checks"][step], job["attestation"]))
                self.assertNotIn("simulated", job["message"])  # Details go to the journal, not the desktop.

    def test_virtualisation_turned_off_is_said_plainly(self):
        builder = self.builder(NixRunner(fail_step="vm-test", error="qemu-system-x86_64: Could not access KVM kernel module: No such file or directory"))
        candidate_id = builder.build(self.fx.uid, {"changes": CHANGES})["candidate"]["candidateId"]
        builder.run_job(candidate_id)
        job = builder.status(self.fx.uid, {"candidateId": candidate_id})
        self.assertEqual("failed", job["state"])
        self.assertIn("virtualisation", job["message"])
        self.assertIn("BIOS/UEFI", job["message"])

    def test_callers_and_requests_are_checked(self):
        builder = self.builder(NixRunner())
        with self.assertRaises(Denied):
            builder.build(self.fx.uid + 1, {"changes": CHANGES})
        with self.assertRaises(Denied):
            builder.build(self.fx.uid, {"changes": [{"option": "imports", "enabled": True}]})
        with self.assertRaises(Denied):
            builder.build(self.fx.uid, {"changes": CHANGES, "toplevel": TOPLEVEL})
        candidate_id = builder.build(self.fx.uid, {"changes": CHANGES})["candidate"]["candidateId"]
        builder.allowed_uids = frozenset({self.fx.uid, self.fx.uid + 1})
        with self.assertRaises(Denied):
            builder.status(self.fx.uid + 1, {"candidateId": candidate_id})

    def test_baseline_change_before_the_build_fails_it(self):
        builder = self.builder(NixRunner())
        candidate_id = builder.build(self.fx.uid, {"changes": CHANGES})["candidate"]["candidateId"]
        self.fx.observer.baseline = OTHER
        builder.run_job(candidate_id)
        self.assertEqual("failed", builder.status(self.fx.uid, {"candidateId": candidate_id})["state"])

    def test_interrupted_job_is_failed_after_a_restart_and_can_be_retried(self):
        builder = self.builder(NixRunner())
        candidate_id = builder.build(self.fx.uid, {"changes": CHANGES})["candidate"]["candidateId"]
        restarted = self.builder(NixRunner())
        self.assertEqual("failed", restarted.status(self.fx.uid, {"candidateId": candidate_id})["state"])
        self.assertEqual("queued", restarted.build(self.fx.uid, {"changes": CHANGES})["job"]["state"])


class StageServiceTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture()
        self.addCleanup(self.fx.close)
        candidate = self.fx.submitted()
        self.fx.broker.register_approval(candidate["candidateId"], self.fx.uid)
        self.decision = self.fx.broker.review(self.fx.uid, {"candidateId": candidate["candidateId"],
                                                            "attestation": self.fx.attest(candidate)})["decision"]
        self.staged = []

        def stager(broker, decision, **kwargs):
            self.staged.append((decision, kwargs["bootloader"]))
            broker.claim_for_staging(decision)
            return {"toplevel": TOPLEVEL, "baseline": BASELINE}
        self.service = StageService(self.fx.broker, bootloader="systemd-boot", stager=stager, pre_stage=lambda: None)

    def test_stages_the_callers_own_permitted_decision_once(self):
        result = self.service.stage(self.fx.uid, {"decision": self.decision})
        self.assertEqual({"staged": True, "toplevel": TOPLEVEL, "baseline": BASELINE}, result)
        self.assertEqual([(self.decision, "systemd-boot")], self.staged)
        with self.assertRaises(Denied):
            self.service.stage(self.fx.uid, {"decision": self.decision})

    def test_other_callers_and_unknown_or_denied_decisions_are_refused(self):
        self.service.allowed_uids = frozenset({self.fx.uid, self.fx.uid + 1})
        for uid, payload in ((self.fx.uid + 1, {"decision": self.decision}), (self.fx.uid, {"decision": "0" * 64}),
                             (self.fx.uid, {"decision": "not-a-hash"}), (self.fx.uid + 2, {"decision": self.decision})):
            with self.subTest(uid=uid, payload=payload), self.assertRaises(Denied):
                self.service.stage(uid, payload)
        candidate = self.fx.submitted([{"option": "services.flatpak.enable", "enabled": True}])
        self.fx.broker.register_approval(candidate["candidateId"], self.fx.uid)
        denied = self.fx.broker.review(self.fx.uid, {"candidateId": candidate["candidateId"],
                                                     "attestation": self.fx.attest(candidate, checks={"evaluate": True, "build": False, "vm-test": True})})
        with self.assertRaises(Denied):
            self.service.stage(self.fx.uid, {"decision": denied["decision"]})
        self.assertEqual([], self.staged)

    def test_outcome_follows_the_staging_log(self):
        self.assertEqual("unknown", self.service.outcome(self.fx.uid, {"decision": self.decision})["state"])
        log = Path(self.fx.state) / "staging.jsonl"
        for event, expected in (("staged", "staged"), ("confirmed", "applied"), ("fell-back", "rolled-back")):
            with log.open("a") as stream:
                stream.write(json.dumps({"event": event, "decision": self.decision, "at": 1}) + "\n")
                stream.write(json.dumps({"event": "confirmed", "decision": "e" * 64, "at": 2}) + "\n")
            self.assertEqual(expected, self.service.outcome(self.fx.uid, {"decision": self.decision})["state"])


if __name__ == "__main__":
    unittest.main()
