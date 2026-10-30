#!/usr/bin/env python3
"""Post-boot Guardian assessment: confirm a healthy trial boot or restore the baseline.

Runs once per boot (nexus-guardian-boot-check.service). With a pending trial
(running the candidate means its trial-boot entry, or its plain entry):
  - running the candidate and healthy -> make it the bootloader default; confirmed
  - running the candidate but unhealthy -> restore the baseline for next boot
  - running the baseline                -> the trial failed and the bootloader fell
                                           back; restore the baseline profile
The bootloader is the one recorded when the trial was staged (GRUB unless the
record says systemd-boot).
"""
import argparse
import json
import os
from pathlib import Path
import subprocess

from stage_executor import GRUBENV, PROFILE, record, run


def system_health(required_units):
    # Not `is-system-running --wait`: this runs as a boot job and would wait on itself.
    if subprocess.run(["systemctl", "is-active", "--quiet", "multi-user.target"], timeout=60).returncode != 0:
        return False, "multi-user.target inactive"
    failed = subprocess.run(["systemctl", "list-units", "--state=failed", "--no-legend", "--plain"],
                            capture_output=True, text=True, timeout=60).stdout.split()
    if failed:
        return False, "failed units: " + " ".join(name for name in failed if "." in name)
    for unit in required_units:
        if subprocess.run(["systemctl", "is-active", "--quiet", unit], timeout=60).returncode != 0:
            return False, f"{unit} inactive"
    return True, "healthy"


def follow_profile(bootloader, runner, grub_editenv, grubenv, bootctl):
    """Let the bootloader's default follow the system profile again."""
    if bootloader == "systemd-boot":
        # Without the EFI override, loader.conf's default (the profile's newest generation) applies.
        runner([bootctl, "set-default", ""])
    else:
        runner([grub_editenv, grubenv, "set", "saved_entry=0"])


def restore_baseline(baseline, runner, follow):
    runner(["nix-env", "--profile", PROFILE, "--set", baseline])
    runner([os.path.join(baseline, "bin", "switch-to-configuration"), "boot"])
    follow()


def check(state_dir, *, current=None, health=None, runner=run, grub_editenv="grub-editenv", grubenv=GRUBENV,
          bootctl="bootctl", required_units=()):
    pending_path = Path(state_dir) / "pending-boot.json"
    if not pending_path.exists():
        return "nothing-pending"
    pending = json.loads(pending_path.read_text(encoding="utf-8"))
    bootloader = pending.get("bootloader", "grub")

    def follow():
        follow_profile(bootloader, runner, grub_editenv, grubenv, bootctl)
    current = current if current is not None else os.path.realpath("/run/current-system")
    # Trials staged before trial-boot entries existed record only the toplevel.
    if current in (pending["toplevel"], pending.get("trialSystem")):
        ok, reason = (health or system_health)(required_units)
        if ok:
            follow()  # Default entry now follows the new profile.
            outcome = "confirmed"
        else:
            restore_baseline(pending["baseline"], runner, follow)
            outcome = "rolled-back"
        record(state_dir, {"event": outcome, "decision": pending["decision"], "reason": reason})
    elif current == pending["baseline"]:
        restore_baseline(pending["baseline"], runner, follow)
        outcome = "fell-back"
        record(state_dir, {"event": outcome, "decision": pending["decision"], "reason": "trial boot did not complete"})
    else:
        record(state_dir, {"event": "unexpected-generation", "decision": pending["decision"], "current": current})
        return "unexpected-generation"  # Leave the pending record for an administrator.
    pending_path.unlink()
    return outcome


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state-dir", default="/var/lib/nexus-guardian")
    parser.add_argument("--require-unit", action="append", default=[])
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Root UID required")
    print(check(args.state_dir, required_units=args.require_unit))


if __name__ == "__main__":
    main()
