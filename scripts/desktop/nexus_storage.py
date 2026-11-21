#!/usr/bin/env python3
"""Storage (Settings, Storage): what is using the disk, and making room.

  nexus-storage report         the disk, and how much each kind of file uses (JSON)
  nexus-storage empty-trash    empty the trash
  nexus-storage clear-cache    remove what apps keep in ~/.cache (they make it again when needed)
  nexus-storage clean-system   (root, through pkexec: nexus-storage-clean) remove system versions
                               older than two weeks (the one running and the two before it stay)
                               and apps' unused runtimes, then free the space they used
"""
import json
import os
import time
from pathlib import Path
import shutil
import subprocess
import sys

HOME = Path(os.environ.get("HOME", "~")).expanduser()
DATA = Path(os.environ.get("XDG_DATA_HOME", HOME / ".local/share"))
CACHE = Path(os.environ.get("XDG_CACHE_HOME", HOME / ".cache"))
TRASH = DATA / "Trash"
PROFILES = Path("/nix/var/nix/profiles")
KEEP_CACHE = {"nexusos"}        # Nexus's own (models being downloaded): not "cache" to the person
FOLDERS = [
    ("Documents", HOME / "Documents"), ("Downloads", HOME / "Downloads"), ("Pictures", HOME / "Pictures"),
    ("Videos", HOME / "Videos"), ("Music", HOME / "Music"), ("Desktop", HOME / "Desktop"),
    ("Apps from Flathub", DATA / "flatpak"), ("Trash", TRASH), ("Caches", CACHE),
    ("Local AI models", DATA / "nexusos"),
]


def run(command, timeout=None):
    r = subprocess.run(command, capture_output=True, text=True, check=False, timeout=timeout)
    return r.returncode, r.stdout, r.stderr


def size_of(path, runner=run):
    if not Path(path).exists():
        return 0
    try:
        status, out, _ = runner(["du", "-s", "-B1", "-x", str(path)], timeout=120)
    except subprocess.TimeoutExpired:
        return 0
    try:
        return int(out.split()[0]) if status in (0, 1) and out else 0
    except ValueError:
        return 0


def volume(path):
    st = os.statvfs(path)
    return {"path": str(path), "totalBytes": st.f_blocks * st.f_frsize, "freeBytes": st.f_bavail * st.f_frsize,
            "usedBytes": (st.f_blocks - st.f_bfree) * st.f_frsize, "device": os.stat(path).st_dev}


def generations(profiles=None):
    """system-N-link names, oldest first (by N: 10 comes after 9)."""
    profiles = profiles or PROFILES
    if not profiles.is_dir():
        return []
    names = [p.name for p in profiles.glob("system-*-link") if p.name[len("system-"):-len("-link")].isdigit()]
    return sorted(names, key=lambda n: int(n[len("system-"):-len("-link")]))


def report(runner=run):
    volumes, seen = [], set()
    for p in ("/", str(HOME)):
        try:
            v = volume(p)
        except OSError:
            continue
        if v["device"] not in seen:
            seen.add(v["device"])
            volumes.append({k: v[k] for k in ("path", "totalBytes", "freeBytes", "usedBytes")})
    kinds = [{"label": label, "path": str(path), "bytes": size_of(path, runner)} for label, path in FOLDERS]
    return {"volumes": volumes, "kinds": [k for k in kinds if k["bytes"] > 0], "systemVersions": len(generations())}


def empty_dir(path, keep=()):
    freed = 0
    if not Path(path).is_dir():
        return 0
    for entry in Path(path).iterdir():
        if entry.name in keep:
            continue
        try:
            if entry.is_dir() and not entry.is_symlink():
                freed += sum(f.stat().st_blocks * 512 for f in entry.rglob("*") if f.is_file() and not f.is_symlink())
                shutil.rmtree(entry, ignore_errors=True)
            else:
                freed += entry.lstat().st_blocks * 512
                entry.unlink()
        except OSError:
            continue
    return freed


def empty_trash():
    freed = empty_dir(TRASH / "files") + empty_dir(TRASH / "info") + empty_dir(TRASH / "expunged")
    return {"message": "The trash is empty.", "freedBytes": freed}


def clear_cache():
    freed = empty_dir(CACHE, keep=KEEP_CACHE)
    return {"message": "Caches cleared. Apps make what they need again as you use them.", "freedBytes": freed}


def clean_system(runner=run):
    if os.geteuid() != 0:
        return {"error": "Removing earlier system versions needs an administrator."}
    before = volume("/nix")["freeBytes"] if Path("/nix").exists() else 0
    # Older than two weeks, but always the running version and the two before it (for Earlier versions).
    gens = generations()
    keep = set(gens[-3:])
    try:      # the version in use, even after going back to an earlier one
        keep.add(os.readlink(PROFILES / "system"))
    except OSError:
        pass
    for name in gens:
        if name in keep:
            continue
        number = name[len("system-"):-len("-link")]
        link = PROFILES / name
        try:
            age = time.time() - link.lstat().st_mtime
        except OSError:
            continue
        if age > 14 * 86400 and number.isdigit():
            runner(["nix-env", "--profile", str(PROFILES / "system"), "--delete-generations", number])
    runner(["flatpak", "uninstall", "--system", "--unused", "--noninteractive", "-y"])
    status, _, err = runner(["nix-store", "--gc"])
    if status != 0:
        return {"error": "The space could not be freed: " + (err.strip().splitlines() or ["nix-store failed"])[-1]}
    # The start-up menu lists only the versions that are left.
    runner(["/run/current-system/bin/switch-to-configuration", "boot"])
    after = volume("/nix")["freeBytes"] if Path("/nix").exists() else 0
    return {"message": "Earlier system versions older than two weeks were removed.", "freedBytes": max(0, after - before)}


def main(argv):
    ops = {"report": report, "empty-trash": empty_trash, "clear-cache": clear_cache, "clean-system": clean_system}
    if len(argv) != 1 or argv[0] not in ops:
        print(json.dumps({"error": "Use: " + ", ".join(ops)}))
        return 2
    result = ops[argv[0]]()
    print(json.dumps(result))
    return 1 if "error" in result else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
