import json
import os
import tempfile
import unittest
from pathlib import Path

from support import BASELINE, BrokerFixture, TOPLEVEL
import grub_entries
from boot_check import check
from stage_executor import PROFILE, StagingError, stage


def grub_cfg(default, generations):
    """A grub.cfg in the layout NixOS install-grub.pl generates."""
    lines = ['if [ -s $prefix/grubenv ]; then', '  load_env', 'fi',
             'menuentry "NixOS - Default" {', '  savedefault',
             f'  linux /nix/store/k-linux/bzImage init={default}/init loglevel=4', '}',
             'submenu "NixOS - All configurations" --class submenu {']
    for number, path in generations:
        lines += [f'menuentry "NixOS - Configuration {number}" {{', '  savedefault',
                  f'  linux /nix/store/k-linux/bzImage init={path}/init loglevel=4', '}']
    lines.append('}')
    return "\n".join(lines) + "\n"


class GrubEntryTests(unittest.TestCase):
    def test_locates_generations_by_exact_init(self):
        cfg = grub_cfg(TOPLEVEL, [(2, TOPLEVEL), (1, BASELINE)])
        self.assertEqual("0", grub_entries.entry_for(cfg, TOPLEVEL))
        self.assertEqual("1>0", grub_entries.entry_for(cfg, TOPLEVEL, prefer_submenu=True))
        self.assertEqual("1>1", grub_entries.entry_for(cfg, BASELINE, prefer_submenu=True))
        self.assertIsNone(grub_entries.entry_for(cfg, "/nix/store/" + "9" * 32 + "-nixos-system-x"))

    def test_prefix_paths_do_not_match(self):
        cfg = grub_cfg(BASELINE + "-extra", [(1, BASELINE + "-extra")])
        self.assertIsNone(grub_entries.entry_for(cfg, BASELINE))


class Runner:
    def __init__(self, fail_on=None):
        self.commands, self.fail_on = [], fail_on

    def __call__(self, command):
        self.commands.append(command)
        if self.fail_on and self.fail_on(command):
            raise OSError("simulated failure")


class StagingTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture(store_ok=True)
        self.addCleanup(self.fx.close)
        self.store = tempfile.TemporaryDirectory()
        self.addCleanup(self.store.cleanup)
        candidate = self.fx.submitted()
        self.fx.broker.register_approval(candidate["candidateId"], self.fx.uid)
        self.decision = self.fx.broker.review(self.fx.uid, {"candidateId": candidate["candidateId"],
                                                            "attestation": self.fx.attest(candidate)})["decision"]
        self.cfg = Path(self.store.name) / "grub.cfg"
        self.cfg.write_text(grub_cfg(TOPLEVEL, [(2, TOPLEVEL), (1, BASELINE)]))
        # stage() requires the approved toplevel to look like a NixOS system.
        self.isfile = os.path.isfile
        os.path.isfile = lambda path: path == os.path.join(TOPLEVEL, "bin", "switch-to-configuration") or self.isfile(path)
        self.addCleanup(lambda: setattr(os.path, "isfile", self.isfile))

    def stage(self, runner, **kwargs):
        return stage(self.fx.broker, self.decision, runner=runner, observer=self.fx.observer,
                     grub_cfg_path=self.cfg, grubenv="/tmp/grubenv", **kwargs)

    def test_stages_for_one_trial_boot_with_baseline_fallback(self):
        runner = Runner()
        state = self.stage(runner)
        self.assertEqual([["nix-env", "--profile", PROFILE, "--set", TOPLEVEL],
                          [TOPLEVEL + "/bin/switch-to-configuration", "boot"],
                          ["grub-editenv", "/tmp/grubenv", "set", "saved_entry=1>1", "next_entry=0"]], runner.commands)
        self.assertEqual(BASELINE, state["baseline"])
        pending = Path(self.fx.state) / "pending-boot.json"
        self.assertEqual(0o600, pending.stat().st_mode & 0o777)

    def test_never_switches_the_running_system(self):
        runner = Runner()
        self.stage(runner)
        flat = [part for command in runner.commands for part in command]
        for forbidden in ("switch", "test", "dry-activate", "nixos-rebuild"):
            self.assertNotIn(forbidden, flat)

    def test_decision_can_only_be_staged_once(self):
        self.stage(Runner())
        (Path(self.fx.state) / "pending-boot.json").unlink()
        with self.assertRaises(Exception):
            self.stage(Runner())

    def test_refuses_while_a_trial_is_pending(self):
        (Path(self.fx.state) / "pending-boot.json").write_text("{}")
        with self.assertRaises(StagingError):
            self.stage(Runner())

    def test_baseline_change_after_review_refuses(self):
        self.fx.observer.baseline = "/nix/store/" + "5" * 32 + "-nixos-system-lab-26.05"
        runner = Runner()
        with self.assertRaises(StagingError):
            self.stage(runner)
        self.assertEqual([], runner.commands)

    def test_failed_bootloader_install_restores_baseline(self):
        runner = Runner(fail_on=lambda command: command == [TOPLEVEL + "/bin/switch-to-configuration", "boot"])
        with self.assertRaises(OSError):
            self.stage(runner)
        self.assertEqual(["nix-env", "--profile", PROFILE, "--set", BASELINE], runner.commands[2])
        self.assertFalse((Path(self.fx.state) / "pending-boot.json").exists())
        events = [json.loads(line)["event"] for line in (Path(self.fx.state) / "staging.jsonl").read_text().splitlines()]
        self.assertEqual(["stage-failed"], events)

    def test_backup_runs_before_staging_and_failure_aborts(self):
        order = []
        runner = Runner()
        runner_calls = lambda command: (order.append("stage"), runner(command))
        self.stage(runner_calls, pre_stage=lambda: order.append("backup") or {"/home": "snap"})
        self.assertEqual("backup", order[0])
        events = [json.loads(line)["event"] for line in (Path(self.fx.state) / "staging.jsonl").read_text().splitlines()]
        self.assertEqual(["backed-up", "staged"], events)

    def test_failed_backup_stages_nothing(self):
        runner = Runner()
        def failing():
            raise OSError("disk full")
        with self.assertRaises(StagingError):
            self.stage(runner, pre_stage=failing)
        self.assertEqual([], runner.commands)
        self.assertFalse((Path(self.fx.state) / "pending-boot.json").exists())

    def test_missing_grub_entry_restores_baseline(self):
        self.cfg.write_text(grub_cfg(BASELINE, [(1, BASELINE)]))
        runner = Runner()
        with self.assertRaises(StagingError):
            self.stage(runner)
        self.assertEqual(["nix-env", "--profile", PROFILE, "--set", BASELINE], runner.commands[-2])


class BootCheckTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.state = Path(self.dir.name)
        (self.state / "pending-boot.json").write_text(json.dumps(
            {"decision": "d" * 64, "toplevel": TOPLEVEL, "baseline": BASELINE, "trialEntry": "0", "fallbackEntry": "1>1"}))

    def run_check(self, current, healthy=True):
        runner = Runner()
        outcome = check(self.state, current=current, health=lambda _: (healthy, "test"), runner=runner,
                        grubenv="/tmp/grubenv")
        return outcome, runner.commands

    def test_nothing_pending(self):
        (self.state / "pending-boot.json").unlink()
        self.assertEqual(("nothing-pending", []), self.run_check(TOPLEVEL))

    def test_healthy_trial_is_confirmed_as_default(self):
        outcome, commands = self.run_check(TOPLEVEL)
        self.assertEqual("confirmed", outcome)
        self.assertEqual([["grub-editenv", "/tmp/grubenv", "set", "saved_entry=0"]], commands)
        self.assertFalse((self.state / "pending-boot.json").exists())

    def test_unhealthy_trial_restores_baseline(self):
        outcome, commands = self.run_check(TOPLEVEL, healthy=False)
        self.assertEqual("rolled-back", outcome)
        self.assertEqual(["nix-env", "--profile", PROFILE, "--set", BASELINE], commands[0])
        self.assertEqual([BASELINE + "/bin/switch-to-configuration", "boot"], commands[1])

    def test_fallback_boot_restores_baseline_profile(self):
        outcome, commands = self.run_check(BASELINE)
        self.assertEqual("fell-back", outcome)
        self.assertEqual(["nix-env", "--profile", PROFILE, "--set", BASELINE], commands[0])

    def test_unexpected_generation_is_left_for_an_administrator(self):
        outcome, commands = self.run_check("/nix/store/" + "7" * 32 + "-nixos-system-other")
        self.assertEqual("unexpected-generation", outcome)
        self.assertEqual([], commands)
        self.assertTrue((self.state / "pending-boot.json").exists())


if __name__ == "__main__":
    unittest.main()
