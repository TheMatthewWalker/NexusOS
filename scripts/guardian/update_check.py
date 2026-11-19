#!/usr/bin/env python3
"""Look for security updates and NexusOS updates and hand them to Guardian (nexus-update-check.service, root).

A computer installed from a NexusOS release follows its release branch
(github:<owner>/<repo>/release in /etc/nixos/flake.nix): NexusOS moves there
first (`nix flake update nexusos`), bringing the nixpkgs it was tested with,
then nixpkgs moves on its branch as below. Either is one update, offered,
built, tested and approved the same way.

Weekly (and when the person asks Nexus to check), once online: copies
/etc/nixos into a root-owned work directory, moves only NexusOS's nixpkgs
input forward on its release branch (`nix flake update nexusos/nixpkgs`; the
branch is whatever the lock already follows, nixos-26.05), and if the lock
changed records it as the update waiting (system_update.py). NexusOS's own
source and /etc/nixos are never changed here: the update is built, tested and
signed by Guardian's builder like any change, the person approves it with
their password, and /etc/nixos/flake.lock is replaced only after its trial
boot is confirmed (boot_check.py).

Refuses to start when /nix has less than --min-free-gib free, and says so in
status.json. The work directory is removed afterwards.

  nexus-update-check --dir /var/lib/nexus-updates --host-flake /etc/nixos [--wait-online nm-online]

Exit status 75 means offline: the unit retries later.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

import system_update
from system_update import UpdateError

NIX = ["nix", "--extra-experimental-features", "nix-command flakes"]
OFFLINE = 75


def run_command(command, timeout):
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        return 124, "", "timed out"
    return result.returncode, result.stdout, "\n".join(result.stderr.splitlines()[-20:])


def _json(value):
    return (json.dumps(value, sort_keys=True, indent=1) + "\n").encode("utf-8")


def _prune(locks, keep):
    """Keep the newest few locks: an update approved just before a newer one was found can still be staged."""
    files = sorted(locks.glob("*.lock"), key=lambda path: path.stat().st_mtime, reverse=True)
    for old in files[keep:]:
        old.unlink(missing_ok=True)


def _keep_source(updates_dir, flake, lock_hash, locks, runner):
    """A garbage-collector root for the fetched nixpkgs, until its lock is pruned.

    The builder has no network: it evaluates the update from the store, so the
    source this check fetched must survive the weekly garbage collection until
    it is built. (Once installed, the system itself refers to it.)"""
    roots = updates_dir / "roots"
    roots.mkdir(mode=0o700, exist_ok=True)
    for root in roots.iterdir():
        if not (locks / f"{root.name}.lock").exists():
            root.unlink(missing_ok=True)
    status, out, err = runner(NIX + ["flake", "archive", "--json", f"path:{flake}"], 600)
    try:
        source = json.loads(out)["inputs"][system_update.NEXUS_INPUT]["inputs"][system_update.NIXPKGS_INPUT]["path"]
    except (ValueError, KeyError, TypeError):
        source = None
    if status != 0 or not isinstance(source, str) or not source.startswith("/nix/store/"):
        print(f"nexus-update-check: could not keep the nixpkgs source: {err}", file=sys.stderr, flush=True)
        return
    runner(NIX + ["build", "--out-link", str(roots / lock_hash), source], 120)


def follows_release(lock_bytes):
    """Whether this computer's NexusOS follows a release branch (installed from a release), not a local copy."""
    try:
        lock = json.loads(lock_bytes)
        return system_update.release(lock["nodes"][system_update._nexus_name(lock)]) is not None
    except (ValueError, KeyError, TypeError, UpdateError):
        return False


def check(updates_dir, host_dir, *, runner=run_command, free=system_update.free_bytes, clock=time.time,
          online=lambda: True, minimum=system_update.MIN_FREE_BYTES):
    """One check. Returns the status record it wrote (status.json)."""
    updates_dir, host_dir = Path(updates_dir), Path(host_dir)
    updates_dir.mkdir(mode=0o700, exist_ok=True)
    os.chmod(updates_dir, 0o700)
    now = int(clock())

    def report(state, message="", **extra):
        record = {"state": state, "checkedAt": now, "message": message, **extra}
        system_update.write_file(updates_dir / "status.json", _json(record))
        return record

    space = free()
    try:
        system_update.require_space(space, minimum)
    except UpdateError as error:
        return report("low-disk", str(error), freeBytes=space)
    if not online():
        return report("offline", "Not connected to the internet; Guardian checks again later.")

    work = updates_dir / "work"
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(mode=0o700)
    try:
        flake = work / "flake"
        shutil.copytree(host_dir, flake, symlinks=True)
        old = (host_dir / "flake.lock").read_bytes()
        if follows_release(old):
            status, _, err = runner(NIX + ["flake", "update", system_update.NEXUS_INPUT, "--flake", f"path:{flake}"], 1800)
            if status != 0:
                print(f"nexus-update-check: nix flake update nexusos failed: {err}", file=sys.stderr, flush=True)
                return report("failed", "Could not reach NexusOS's releases. Guardian checks again later.")
        status, _, err = runner(NIX + ["flake", "update", f"{system_update.NEXUS_INPUT}/{system_update.NIXPKGS_INPUT}",
                                       "--flake", f"path:{flake}"], 1800)
        if status != 0:
            print(f"nexus-update-check: nix flake update failed: {err}", file=sys.stderr, flush=True)
            return report("failed", "Could not fetch the latest NixOS packages. Guardian checks again later.")
        new = (flake / "flake.lock").read_bytes()
        try:
            change = system_update.compare_update(old, new)
        except UpdateError as error:
            print(f"nexus-update-check: refused the updated lock: {error}", file=sys.stderr, flush=True)
            return report("failed", "The update did not have the expected form, so it was not offered.")
        if change is None:
            (updates_dir / "update.json").unlink(missing_ok=True)
            return report("up-to-date", "This computer has the latest security updates.")
        locks = updates_dir / "locks"
        locks.mkdir(mode=0o700, exist_ok=True)
        lock_hash = system_update.sha256(new)
        system_update.write_file(locks / f"{lock_hash}.lock", new)
        _prune(locks, system_update.KEEP_LOCKS)
        _keep_source(updates_dir, flake, lock_hash, locks, runner)
        system_update.write_file(updates_dir / "update.json", _json({
            "lockHash": lock_hash, "previousLockHash": system_update.sha256(old), **change, "checkedAt": now}))
        if "nexusos" in change:
            return report("available", "A NexusOS update is available, with security updates.", lockHash=lock_hash)
        return report("available", "Security updates are available.", lockHash=lock_hash)
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dir", type=Path, default=system_update.UPDATES_DIR)
    parser.add_argument("--host-flake", type=Path, default=system_update.HOST_FLAKE_DIR)
    parser.add_argument("--min-free-gib", type=int, default=10)
    parser.add_argument("--wait-online", help="a command (nm-online) that waits for a connection; offline exits 75")
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Root UID required")

    def online():
        if not args.wait_online:
            return True
        return subprocess.run([args.wait_online, "-q", "--timeout=60"], check=False, timeout=90).returncode == 0
    try:
        record = check(args.dir, args.host_flake, online=online, minimum=args.min_free_gib * 1024 ** 3)
    except (OSError, UpdateError) as error:
        raise SystemExit(f"nexus-update-check: {error}")
    print(f"nexus-update-check: {record['state']}: {record['message']}")
    if record["state"] == "offline":
        sys.exit(OFFLINE)


if __name__ == "__main__":
    main()
