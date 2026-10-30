#!/usr/bin/env python3
"""Guardian staging executor (ADR-002): stage a broker-approved system for ONE trial boot.

The only system mutation in Guardian. Root-only. It never switches the running
system: it sets the system profile, runs `switch-to-configuration boot`, then
keeps the bootloader's default on the observed baseline and sets a one-time
entry for the candidate's trial-boot specialisation (guardian-trial: the same
system, with `panic=N boot.panic_on_fail` on that entry only):

  GRUB          saved_entry = baseline, next_entry = candidate (grub-editenv)
  systemd-boot  LoaderEntryDefault = baseline, LoaderEntryOneShot = candidate (bootctl)

If the candidate never reaches a healthy boot (panic, hang, reset), the
bootloader returns to the baseline automatically; boot_check.py confirms or
rolls back after the next boot.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time

import grub_entries
import systemd_boot
from baseline_inspect import TRIAL_SPECIALISATION, inspect
from guardian_schema import STORE_PATH

PROFILE = "/nix/var/nix/profiles/system"
GRUB_CFG = "/boot/grub/grub.cfg"
GRUBENV = "/boot/grub/grubenv"
ESP = "/boot"
BOOTLOADERS = ("grub", "systemd-boot")


class StagingError(Exception):
    pass


def run(command):
    subprocess.run(command, check=True, timeout=600)


def record(state_dir, event):
    path = Path(state_dir) / "staging.jsonl"
    fd = os.open(path, os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "a", encoding="utf-8") as stream:
        stream.write(json.dumps({**event, "at": int(time.time())}, sort_keys=True) + "\n")


def trial_system(toplevel):
    """The store path of toplevel's trial-boot specialisation, or None if it has none."""
    path = os.path.realpath(os.path.join(toplevel, "specialisation", TRIAL_SPECIALISATION))
    if STORE_PATH.fullmatch(path) is None or not os.path.isfile(os.path.join(path, "init")):
        return None
    return path


def _grub_trial(runner, trial_toplevel, baseline, grub_cfg_path, grubenv, grub_editenv):
    cfg = Path(grub_cfg_path).read_text(encoding="utf-8")
    trial = grub_entries.entry_for(cfg, trial_toplevel)
    fallback = grub_entries.entry_for(cfg, baseline, prefer_submenu=True)
    if trial is None or fallback is None:
        raise StagingError("Could not locate both generations in grub.cfg")
    runner([grub_editenv, grubenv, "set", f"saved_entry={fallback}", f"next_entry={trial}"])
    return trial, fallback


def _systemd_boot_trial(runner, trial_toplevel, baseline, esp, bootctl):
    directory = Path(esp) / "loader" / "entries"
    trial = systemd_boot.entry_for(directory, trial_toplevel, TRIAL_SPECIALISATION)
    fallback = systemd_boot.entry_for(directory, baseline)
    if trial is None or fallback is None or trial == fallback:
        raise StagingError("Could not locate both generations among the systemd-boot entries")
    # The EFI default outranks loader.conf (which now names the candidate), and
    # the one-shot entry is consumed by systemd-boot as it starts the trial.
    runner([bootctl, "set-default", fallback])
    runner([bootctl, "set-oneshot", trial])
    return trial, fallback


def stage(broker, decision_hash, *, runner=run, observer=inspect, grub_cfg_path=GRUB_CFG,
          grubenv=GRUBENV, grub_editenv="grub-editenv", pre_stage=None, bootloader="grub", esp=ESP,
          bootctl="bootctl", trial_of=trial_system):
    if bootloader not in BOOTLOADERS:
        raise StagingError("Unsupported bootloader")
    pending = Path(broker.state_dir) / "pending-boot.json"
    if pending.exists():
        raise StagingError("A staged trial boot is already pending; reboot or resolve it first")
    observation = observer()
    if observation.get("verified") is not True:
        raise StagingError("Running system could not be verified")
    baseline = observation["generationStorePath"]
    toplevel = broker.claim_for_staging(decision_hash)  # Once only, permitted decisions only.
    candidate_baseline = broker.decision_baseline(decision_hash)
    if candidate_baseline != baseline:
        record(broker.state_dir, {"event": "stage-refused", "decision": decision_hash, "reason": "baseline changed"})
        raise StagingError("The running system changed since review; resubmit the candidate")
    if STORE_PATH.fullmatch(toplevel or "") is None or not os.path.isfile(os.path.join(toplevel, "bin", "switch-to-configuration")):
        raise StagingError("Approved toplevel is not a NixOS system in the local store")
    # Only the trial's own entry resets on a failed boot; without it a broken
    # trial would stop at its failure instead of falling back unattended.
    trial_toplevel = trial_of(toplevel)
    if trial_toplevel is None:
        raise StagingError("Approved system has no Guardian trial-boot entry (services.nexus-guardian.staging)")
    if pre_stage is not None:
        try:
            snapshots = pre_stage()
        except Exception as error:  # noqa: BLE001 - any backup failure must stop staging
            record(broker.state_dir, {"event": "stage-refused", "decision": decision_hash, "reason": "backup failed"})
            raise StagingError(f"User-data backup failed; nothing staged ({error})") from None
        record(broker.state_dir, {"event": "backed-up", "decision": decision_hash, "snapshots": snapshots})

    runner(["nix-env", "--profile", PROFILE, "--set", toplevel])
    try:
        runner([os.path.join(toplevel, "bin", "switch-to-configuration"), "boot"])
        if bootloader == "grub":
            trial, fallback = _grub_trial(runner, trial_toplevel, baseline, grub_cfg_path, grubenv, grub_editenv)
        else:
            trial, fallback = _systemd_boot_trial(runner, trial_toplevel, baseline, esp, bootctl)
    except BaseException:
        # Leave the machine booting the baseline exactly as before.
        runner(["nix-env", "--profile", PROFILE, "--set", baseline])
        runner([os.path.join(baseline, "bin", "switch-to-configuration"), "boot"])
        if bootloader == "systemd-boot":
            _forget_efi_entries(runner, bootctl)
        record(broker.state_dir, {"event": "stage-failed", "decision": decision_hash, "toplevel": toplevel})
        raise
    state = {"decision": decision_hash, "toplevel": toplevel, "trialSystem": trial_toplevel,
             "baseline": baseline, "bootloader": bootloader,
             "trialEntry": trial, "fallbackEntry": fallback, "stagedAt": int(time.time())}
    fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(state, stream, sort_keys=True)
    record(broker.state_dir, {"event": "staged", **state})
    return state


def _forget_efi_entries(runner, bootctl):
    """Clear Guardian's EFI overrides so loader.conf's default applies again (best effort)."""
    for verb in ("set-oneshot", "set-default"):
        try:
            runner([bootctl, verb, ""])
        except Exception:  # noqa: BLE001 - firmware without the variable is already clean
            pass


BACKUP_CONFIG = Path("/etc/nexus-guardian/backup.json")


def backup_hook(config_path=BACKUP_CONFIG):
    """Snapshot configured user-data paths before staging (nexus_backup.py), if configured."""
    if not config_path.exists():
        return None
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backup"))
    import nexus_backup
    config = json.loads(config_path.read_text(encoding="utf-8"))

    def run_backups():
        taken = {}
        for source in config["paths"]:
            repo = Path(config["repository"]) / source.strip("/").replace("/", "_")
            taken[source] = nexus_backup.snapshot(source, repo, keep=config.get("keep", 5))
        return taken
    return run_backups


def main():
    from root_approval_issuer import build_broker, load_config
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=Path("/etc/nexus-guardian/config.json"))
    parser.add_argument("--decision", required=True)
    parser.add_argument("--bootloader", choices=BOOTLOADERS, default="grub")
    parser.add_argument("--esp", default=ESP, help="systemd-boot: the EFI system partition's mount point")
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Root UID required")
    try:
        state = stage(build_broker(load_config(args.config)), args.decision, pre_stage=backup_hook(),
                      bootloader=args.bootloader, esp=args.esp)
    except Exception as error:  # noqa: BLE001 - report any refusal plainly, never a traceback with paths
        raise SystemExit(f"Not staged: {error}")
    print(f"Staged {state['toplevel']} for one trial boot; the baseline stays the fallback. Reboot to try it.")


if __name__ == "__main__":
    main()
