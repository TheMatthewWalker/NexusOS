#!/usr/bin/env python3
"""NexusOS user-data snapshots, separate from NixOS system generations.

A NixOS rollback restores the operating system, never /home. This tool keeps
point-in-time copies of user data so a system change can be undone without
data loss, and so data can be recovered independently of the OS.

  snapshot --source DIR --repo REPO [--keep N]
  list     --repo REPO
  verify   --repo REPO --snapshot NAME
  restore  --repo REPO --snapshot NAME --target EMPTY_DIR
  prune    --repo REPO --keep N

Each snapshot is one tar archive with an embedded manifest of SHA-256 hashes.
Archives are written to a temporary name, fsynced and atomically renamed, so
an interrupted or power-lost backup never leaves a partial snapshot visible
and never damages earlier ones. Restore refuses absolute paths, '..', device
files and links that escape the target (Python's tarfile "data" filter).
"""
import argparse
import datetime
import hashlib
import io
import json
import os
from pathlib import Path
import secrets
import stat
import sys
import tarfile

SUFFIX = ".nexus-snapshot.tar"
MANIFEST = ".nexus-manifest.json"


def _fsync_dir(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def _hash(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _repository(path, create=False):
    repo = Path(path)
    if create:
        repo.mkdir(mode=0o700, parents=True, exist_ok=True)
    info = os.stat(repo, follow_symlinks=False)
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise PermissionError("Backup repository must be a private (0700) directory owned by you")
    return repo


def snapshots(repo):
    return sorted(p.name[:-len(SUFFIX)] for p in _repository(repo).iterdir()
                  if p.name.endswith(SUFFIX) and not p.name.startswith("."))


def snapshot(source, repo, *, keep=None, now=None):
    source = Path(source).resolve(strict=True)
    repo = _repository(repo, create=True)
    for stale in repo.glob(".tmp-*"):
        stale.unlink()  # Leftovers from an interrupted run are never valid snapshots.
    stamp = (now or datetime.datetime.now(datetime.timezone.utc)).strftime("%Y%m%dT%H%M%SZ")
    name = f"{stamp}-{secrets.token_hex(3)}"
    temp = repo / f".tmp-{name}"
    manifest = []
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as raw:
        with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as archive:
            for root, dirs, files in os.walk(source, followlinks=False):
                dirs.sort()
                for entry in sorted(dirs) + sorted(files):
                    full = Path(root) / entry
                    relative = full.relative_to(source).as_posix()
                    info = os.lstat(full)
                    if not (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode)):
                        continue  # Sockets, FIFOs and devices are not user data.
                    archive.add(full, arcname="data/" + relative, recursive=False)
                    if stat.S_ISREG(info.st_mode):
                        manifest.append({"path": relative, "size": info.st_size, "sha256": _hash(full)})
            body = json.dumps({"version": 1, "source": str(source), "files": manifest}, sort_keys=True).encode()
            member = tarfile.TarInfo(MANIFEST)
            member.size, member.mode = len(body), 0o600
            archive.addfile(member, io.BytesIO(body))
        raw.flush()
        os.fsync(raw.fileno())
    os.rename(temp, repo / (name + SUFFIX))
    _fsync_dir(repo)
    if keep is not None:
        prune(repo, keep)
    return name


def _open(repo, name):
    if not name or "/" in name or name.startswith("."):
        raise ValueError("Invalid snapshot name")
    return tarfile.open(_repository(repo) / (name + SUFFIX), mode="r")


def verify(repo, name):
    with _open(repo, name) as archive:
        manifest = json.load(archive.extractfile(MANIFEST))
        for item in manifest["files"]:
            stream = archive.extractfile("data/" + item["path"])
            digest = hashlib.sha256()
            for block in iter(lambda: stream.read(1 << 20), b""):
                digest.update(block)
            if digest.hexdigest() != item["sha256"]:
                return False
    return True


def restore(repo, name, target):
    target = Path(target)
    if target.exists() and any(target.iterdir()):
        raise FileExistsError("Restore target must be empty; restoring never overwrites existing data")
    if not verify(repo, name):
        raise ValueError("Snapshot failed verification; refusing to restore it")
    target.mkdir(mode=0o700, parents=True, exist_ok=True)
    with _open(repo, name) as archive:
        members = [m for m in archive.getmembers() if m.name.startswith("data/")]
        for member in members:
            member.name = member.name[len("data/"):]
        archive.extractall(target, members=[m for m in members if m.name], filter="data")
    return target


def prune(repo, keep):
    if keep < 1:
        raise ValueError("Keep at least one snapshot")
    names = snapshots(repo)
    for name in names[:-keep]:
        (Path(repo) / (name + SUFFIX)).unlink()
    _fsync_dir(repo)
    return names[-keep:]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("snapshot", "list", "verify", "restore", "prune"))
    parser.add_argument("--source")
    parser.add_argument("--repo", required=True)
    parser.add_argument("--snapshot")
    parser.add_argument("--target")
    parser.add_argument("--keep", type=int)
    args = parser.parse_args()
    try:
        if args.command == "snapshot":
            print(snapshot(args.source, args.repo, keep=args.keep))
        elif args.command == "list":
            print("\n".join(snapshots(args.repo)))
        elif args.command == "verify":
            ok = verify(args.repo, args.snapshot)
            print("ok" if ok else "CORRUPT")
            return 0 if ok else 1
        elif args.command == "restore":
            print(restore(args.repo, args.snapshot, args.target))
        else:
            print("\n".join(prune(args.repo, args.keep)))
    except (OSError, ValueError, tarfile.TarError, KeyError) as error:
        print(f"nexus-backup: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
