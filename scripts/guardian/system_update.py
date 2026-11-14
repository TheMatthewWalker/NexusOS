#!/usr/bin/env python3
"""Security updates as Guardian changes: the pieces the update check, builder, broker and boot check share.

An update is the host's own configuration, without any overlay, evaluated
with a newer flake.lock in which only nixpkgs moved, along the same release
branch (nixos-26.05). The update check (update_check.py, a root service)
writes that lock into a root-owned directory:

  /var/lib/nexus-updates/            0700 root
    status.json                      the last check: up-to-date, available, low-disk, offline or failed
    update.json                      the update waiting: lockHash, previousLockHash, nixpkgs before/after
    locks/<sha256>.lock              the locks themselves, by hash (the newest few)

Nothing else names a lock: Core and the person send only its hash, and the
builder, broker and staging read the lock from here, by that hash, and check
it again. A lock is offered only while /etc/nixos/flake.lock is still the one
it was made from (previousLockHash), so once a confirmed trial boot has copied
it into /etc/nixos (install_lock), or the person changed /etc/nixos, it is no
longer offered.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import stat

UPDATES_DIR = Path("/var/lib/nexus-updates")
HOST_FLAKE_DIR = Path("/etc/nixos")
# The input this repository's flake follows, and its nixpkgs: the only node an update may move.
NEXUS_INPUT, NIXPKGS_INPUT = "nexusos", "nixpkgs"
MIN_FREE_BYTES = 10 * 1024 ** 3
MAX_LOCK_BYTES = 1024 * 1024
KEEP_LOCKS = 3
HASH = re.compile(r"[0-9a-f]{64}\Z")
REV = re.compile(r"[0-9a-f]{40}\Z")
BRANCH = re.compile(r"nixos-[0-9]{2}\.[0-9]{2}\Z")
PACKAGE = re.compile(r"[A-Za-z0-9+._-]{1,100}\Z")
VERSION = re.compile(r"[A-Za-z0-9+._~, -]{0,100}\Z")
MAX_LISTED = 16
# Listed first in the dialog, in this order, when they changed; then the rest by name.
NOTABLE = (("linux", "Linux"), ("firefox", "Firefox"), ("chromium", "Chromium"), ("libreoffice", "LibreOffice"),
           ("vlc", "VLC"), ("systemd", "systemd"), ("glibc", "glibc"), ("openssl", "OpenSSL"), ("openssh", "OpenSSH"),
           ("nss", "NSS"), ("mesa", "Mesa"), ("curl", "curl"), ("sudo", "sudo"), ("polkit", "polkit"))  # guardian-scan: read-only (package names, nothing is run)
LABELS = dict(NOTABLE)


class UpdateError(ValueError):
    """An update that must not be offered or built; the message is safe to show."""


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def free_bytes(path="/nix/store"):
    info = os.statvfs(path)
    return info.f_bavail * info.f_frsize


def gib(count):
    return f"{count / 1024 ** 3:.1f} GiB"


def require_space(free, minimum=MIN_FREE_BYTES):
    if free < minimum:
        raise UpdateError(f"Not enough free disk space for updates: {gib(free)} free, {gib(minimum)} needed. "
                          "Free some space, then check again.")


# --- the lock ---------------------------------------------------------------

def _nixpkgs_name(lock):
    """The lock's node name for nexusos's nixpkgs, or UpdateError."""
    try:
        root = lock["nodes"][lock["root"]]
        nexus = root["inputs"][NEXUS_INPUT]
        name = lock["nodes"][nexus]["inputs"][NIXPKGS_INPUT]
    except (KeyError, TypeError):
        raise UpdateError("The lock file does not have NexusOS's nixpkgs input") from None
    if not isinstance(nexus, str) or not isinstance(name, str) or name not in lock["nodes"]:
        raise UpdateError("The lock file does not have NexusOS's nixpkgs input")
    return name


def revision(node):
    locked, original = node.get("locked"), node.get("original")
    if not isinstance(locked, dict) or not isinstance(original, dict):
        raise UpdateError("nixpkgs is not locked")
    if (locked.get("type"), locked.get("owner"), locked.get("repo")) != ("github", "NixOS", "nixpkgs") \
            or (original.get("type"), original.get("owner"), original.get("repo")) != ("github", "NixOS", "nixpkgs"):
        raise UpdateError("nixpkgs does not come from NixOS's own repository")
    if not isinstance(original.get("ref"), str) or BRANCH.fullmatch(original["ref"]) is None:
        raise UpdateError("nixpkgs does not follow a NixOS release branch")
    if not isinstance(locked.get("rev"), str) or REV.fullmatch(locked["rev"]) is None \
            or type(locked.get("lastModified")) is not int:
        raise UpdateError("nixpkgs is not locked to a revision")
    return {"rev": locked["rev"], "lastModified": locked["lastModified"]}


def compare(old_bytes, new_bytes):
    """What an updated lock changes: None if nothing, else the nixpkgs revisions, or UpdateError.

    Only nexusos's nixpkgs node may differ, only in what is locked, and only
    forward along the same release branch; any other difference is refused.
    """
    try:
        old, new = json.loads(old_bytes), json.loads(new_bytes)
    except (ValueError, UnicodeDecodeError):
        raise UpdateError("A lock file is not valid JSON") from None
    if old == new:
        return None
    if not isinstance(old, dict) or not isinstance(new, dict) or set(old) != set(new) \
            or any(old[key] != new[key] for key in old if key != "nodes"):
        raise UpdateError("The update changes more than nixpkgs")
    name = _nixpkgs_name(old)
    if _nixpkgs_name(new) != name or not isinstance(old["nodes"], dict) or set(old["nodes"]) != set(new["nodes"]):
        raise UpdateError("The update changes more than nixpkgs")
    if any(old["nodes"][node] != new["nodes"][node] for node in old["nodes"] if node != name):
        raise UpdateError("The update changes more than nixpkgs")
    before, after = old["nodes"][name], new["nodes"][name]
    if set(before) != set(after) or any(before[key] != after[key] for key in before if key != "locked") \
            or before.get("original") != after.get("original"):
        raise UpdateError("The update changes more than what nixpkgs is locked to")
    was, now = revision(before), revision(after)
    if now["lastModified"] < was["lastModified"]:
        raise UpdateError("The update would go back to an older nixpkgs")
    if was == now:
        return None
    return {"branch": before["original"]["ref"], "before": was, "after": now}


# --- the update waiting -----------------------------------------------------

def _check_owner(path, owner, directory=False):
    info = os.lstat(path)
    kind = stat.S_ISDIR if directory else stat.S_ISREG
    if not kind(info.st_mode) or info.st_uid != owner or info.st_mode & 0o022:
        raise UpdateError("Guardian's update files have unsafe ownership or permissions")


def read_lock(updates_dir, lock_hash, *, owner=0):
    """The lock with this hash from the root-owned update directory, checked, or UpdateError."""
    if not isinstance(lock_hash, str) or HASH.fullmatch(lock_hash) is None:
        raise UpdateError("Invalid update")
    updates_dir = Path(updates_dir)
    path = updates_dir / "locks" / f"{lock_hash}.lock"
    try:
        _check_owner(updates_dir, owner, directory=True)
        _check_owner(updates_dir / "locks", owner, directory=True)
        _check_owner(path, owner)
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
        with os.fdopen(fd, "rb") as stream:
            data = stream.read(MAX_LOCK_BYTES + 1)
    except FileNotFoundError:
        raise UpdateError("This update is no longer available; check for updates again") from None
    if len(data) > MAX_LOCK_BYTES or sha256(data) != lock_hash:
        raise UpdateError("The update's lock file does not match its hash")
    return data


def _read_json(path, owner):
    try:
        _check_owner(path, owner)
        if os.lstat(path).st_size > 65536:
            raise UpdateError("Guardian's update record is too large")
        value = json.loads(Path(path).read_text(encoding="utf-8"))
    except FileNotFoundError:
        return None
    except (ValueError, UnicodeDecodeError):
        raise UpdateError("Guardian's update record is not valid") from None
    return value if isinstance(value, dict) else None


def pending(updates_dir=UPDATES_DIR, host_dir=HOST_FLAKE_DIR, *, owner=0):
    """The update waiting, as {lockHash, previousLockHash, nixpkgs, checkedAt}, or None.

    Only one whose lock is present and matches, whose recorded revisions are
    what the lock says, and that was made from today's /etc/nixos/flake.lock.
    """
    updates_dir = Path(updates_dir)
    try:
        _check_owner(updates_dir, owner, directory=True)
    except FileNotFoundError:
        return None
    record = _read_json(updates_dir / "update.json", owner)
    if record is None:
        return None
    lock_hash, previous = record.get("lockHash"), record.get("previousLockHash")
    if not isinstance(previous, str) or HASH.fullmatch(previous) is None:
        raise UpdateError("Guardian's update record is not valid")
    try:
        current = (Path(host_dir) / "flake.lock").read_bytes()
    except FileNotFoundError:
        return None
    if sha256(current) != previous:
        return None  # Made from another lock: installed already, or /etc/nixos changed since.
    summary = compare(current, read_lock(updates_dir, lock_hash, owner=owner))
    if summary is None or summary != record.get("nixpkgs"):
        raise UpdateError("Guardian's update record does not match its lock file")
    checked = record.get("checkedAt")
    return {"lockHash": lock_hash, "previousLockHash": previous, "nixpkgs": summary,
            "checkedAt": checked if type(checked) is int else None}


def status(updates_dir=UPDATES_DIR, *, owner=0):
    """The last check's status record (state, checkedAt, message, freeBytes), or None."""
    try:
        record = _read_json(Path(updates_dir) / "status.json", owner)
    except UpdateError:
        return None
    if record is None or record.get("state") not in ("up-to-date", "available", "low-disk", "offline", "failed", "checking"):
        return None
    out = {"state": record["state"]}
    for key in ("checkedAt", "freeBytes"):
        if type(record.get(key)) is int:
            out[key] = record[key]
    if isinstance(record.get("message"), str):
        out["message"] = record["message"][:300]
    return out


def write_file(path, data, mode=0o600):
    """Write atomically: a new file beside it, flushed, then renamed over it."""
    path = Path(path)
    temporary = path.with_name(path.name + ".new")
    temporary.unlink(missing_ok=True)
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
    with os.fdopen(fd, "wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(temporary, mode)
    os.replace(temporary, path)
    directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def install_lock(staged, lock_hash, previous_hash, host_dir=HOST_FLAKE_DIR):
    """After a confirmed trial boot: make the staged lock /etc/nixos/flake.lock (root, 0644, atomic).

    Returns "installed", or "skipped" when /etc/nixos/flake.lock is no longer
    the lock the update was made from (someone changed it meanwhile; it is
    left alone). The staged copy must still match its hash.
    """
    data = Path(staged).read_bytes()
    if sha256(data) != lock_hash:
        raise UpdateError("The staged lock file does not match its hash")
    target = Path(host_dir) / "flake.lock"
    if sha256(target.read_bytes()) != previous_hash:
        return "skipped"
    write_file(target, data, 0o644)
    return "installed"


# --- what changed -----------------------------------------------------------

ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
SIZE = re.compile(r"(?:^|,\s*)[+-]?[0-9.]+\s*[KMG]iB\s*$")


def _version(text):
    text = text.strip()
    if text in ("∅", ""):
        return None
    text = text.replace("ε", "").strip(", ")
    return text if VERSION.fullmatch(text) else None


def diff_summary(text):
    """`nix store diff-closures` output, summarised for the person.

    {"packages": [{name, before, after}] (notable ones first, at most
    MAX_LISTED), "otherPackages": how many more changed version, "added",
    "removed"}. Lines that only change in size are not counted.
    """
    changed, added, removed = {}, 0, 0
    for line in ANSI.sub("", text).splitlines():
        name, sep, rest = line.strip().partition(": ")
        if not sep or PACKAGE.fullmatch(name) is None or "→" not in rest:
            continue
        before, _, after = SIZE.sub("", rest).partition("→")
        before, after = _version(before), _version(after)
        if before is None and after is not None:
            added += 1
        elif after is None and before is not None:
            removed += 1
        elif before is not None and before != after:
            changed[name] = {"name": name, "before": before, "after": after}
    # A wrapped program and its unwrapped build are one update to the person.
    for name in [n for n in changed if n.endswith("-unwrapped") and n[:-len("-unwrapped")] in changed]:
        del changed[name]
    order = {name: index for index, (name, _) in enumerate(NOTABLE)}
    names = sorted(changed, key=lambda n: (order.get(n, len(order)), n))
    listed = [changed[name] for name in names[:MAX_LISTED]]
    return {"packages": listed, "otherPackages": len(names) - len(listed), "added": added, "removed": removed}


def label(name):
    return LABELS.get(name, name)
