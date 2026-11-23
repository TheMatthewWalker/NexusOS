#!/usr/bin/env python3
"""Backups of the person's files to a drive (Settings, Backups), with restic.

Runs as the person, never as root. restic keeps an encrypted, deduplicated
repository on the drive: each backup only adds what changed, it works on any
drive (exFAT, NTFS, ext4), and nothing on the drive can be read without the
backup password.

  nexus-backup status                 what is set up, the last backup, the drive (JSON)
  nexus-backup drives                 drives that could hold backups (JSON)
  nexus-backup setup --dest DIR       use DIR (on a drive) and the password on standard input
  nexus-backup run                    back up now
  nexus-backup auto                   back up if it is a day since the last one and the drive is there
  nexus-backup snapshots              the backups on the drive (JSON)
  nexus-backup restore --snapshot ID [--path PATH]
                                      copy a backup (or one folder of it) into ~/Restored files/<date>

The settings live in ~/.config/nexusos/backup.json; the password in
~/.config/nexusos/backup-password (0600, on the computer's own encrypted
disk). The person is told to keep the password somewhere else too: without
it, the backup cannot be opened on another computer.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

HOME = Path(os.environ.get("HOME", "~")).expanduser()
CONFIG_DIR = Path(os.environ.get("XDG_CONFIG_HOME", HOME / ".config")) / "nexusos"
SETTINGS = CONFIG_DIR / "backup.json"
PASSWORD = CONFIG_DIR / "backup-password"
STATE = CONFIG_DIR / "backup-state.json"
MEDIA = Path(os.environ.get("NEXUS_MEDIA", f"/run/media/{os.environ.get('USER', '')}"))
RESTIC = os.environ.get("NEXUS_RESTIC", "restic")
DAY = 24 * 3600
AUTO_AFTER = 20 * 3600          # "once a day", with room for the hour the computer was off
MIN_PASSWORD = 10
# Never backed up: caches, the trash, things that can be downloaded again.
EXCLUDES = [
    ".cache", ".local/share/Trash", ".local/share/nexusos/colibri", ".local/share/nexusos/large-model",
    ".local/share/containers", ".local/share/flatpak", ".var/app/*/cache", ".ollama", "Restored files",
    ".local/state/nexusos/*.tmp", "*.part",
]
# Keep: the last 7 days, 4 weeks and 12 months.
KEEP = ["--keep-daily", "7", "--keep-weekly", "4", "--keep-monthly", "12"]


class BackupError(Exception):
    """Refused or failed; the message is for the person."""


def run(command, env=None, input_text=None, timeout=None):
    result = subprocess.run(command, capture_output=True, text=True, check=False, input=input_text,
                            env={**os.environ, **(env or {})}, timeout=timeout)
    return result.returncode, result.stdout, result.stderr


def load(path, default):
    try:
        return json.loads(Path(path).read_text())
    except (OSError, ValueError):
        return default


def save(path, data, mode=0o600):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
    with os.fdopen(fd, "w") as stream:
        stream.write(data if isinstance(data, str) else json.dumps(data))
    os.replace(tmp, path)


def repository(dest):
    return str(Path(dest) / "NexusOS backup")


def restic_env():
    if not PASSWORD.exists():
        raise BackupError("Backups are not set up yet.")
    return {"RESTIC_PASSWORD_FILE": str(PASSWORD), "RESTIC_PROGRESS_FPS": "0.2"}


def last_error_line(err):
    lines = [l for l in err.strip().splitlines() if l.strip()]
    return lines[-1] if lines else "restic failed"


def drives(media=MEDIA):
    """Mounted drives (what the file manager mounts under /run/media/$USER), with their free space."""
    out = []
    if not media.is_dir():
        return out
    for entry in sorted(media.iterdir()):
        if not entry.is_dir() or entry.is_symlink():
            continue
        try:
            st = os.statvfs(entry)
            free, total = st.f_bavail * st.f_frsize, st.f_blocks * st.f_frsize
        except OSError:
            continue
        out.append({"path": str(entry), "name": entry.name, "freeBytes": free, "totalBytes": total,
                    "hasBackup": Path(repository(entry), "config").exists()})
    return out


def setup(dest, password, runner=run):
    dest = Path(dest)
    if not dest.is_dir():
        raise BackupError("That drive is not plugged in.")
    if not isinstance(password, str) or len(password) < MIN_PASSWORD or "\n" in password:
        raise BackupError(f"Use a backup password of at least {MIN_PASSWORD} characters.")
    save(PASSWORD, password)
    repo = repository(dest)
    env = restic_env()
    if Path(repo, "config").exists():
        # A backup from before (this computer's, or one being set up again): the password must open it.
        status, _, err = runner([RESTIC, "-r", repo, "cat", "config"], env)
        if status != 0:
            PASSWORD.unlink(missing_ok=True)
            raise BackupError("There is already a backup on this drive, and that password does not open it.")
    else:
        status, _, err = runner([RESTIC, "-r", repo, "init"], env)
        if status != 0:
            PASSWORD.unlink(missing_ok=True)
            raise BackupError("The drive could not be set up for backups: " + last_error_line(err))
    save(SETTINGS, {"dest": str(dest), "repository": repo, "uuid": drive_uuid(dest, runner)})
    return {"message": "Backups go to " + dest.name + ". Keep the backup password somewhere safe too: "
                       "without it, the backup cannot be opened on another computer."}


def drive_uuid(dest, runner=run):
    """The drive's filesystem id, so the automatic backup can find it again when it is plugged in."""
    status, out, _ = runner(["findmnt", "--noheadings", "--output", "UUID", "--target", str(dest)])
    uuid = out.strip() if status == 0 else ""
    return uuid if re.fullmatch(r"[0-9A-Fa-f-]{4,64}", uuid) else None


def mount_drive(s, runner=run):
    """Plugged in but not opened yet: open it (as the file manager would) and remember where it is now."""
    if not s.get("uuid"):
        return s
    status, out, _ = runner(["udisksctl", "mount", "--no-user-interaction", "--block-device", "/dev/disk/by-uuid/" + s["uuid"]])
    m = re.search(r" at (/.+?)\.?$", out.strip()) if status == 0 else None
    if m and Path(m.group(1)).is_dir():
        s = {**s, "dest": m.group(1), "repository": repository(m.group(1))}
        save(SETTINGS, s)
    return s


def settings():
    s = load(SETTINGS, {})
    if not s.get("repository"):
        raise BackupError("Backups are not set up yet.")
    return s


def backup(runner=run, clock=time.time):
    s = settings()
    if not Path(s["dest"]).is_dir():
        raise BackupError("The backup drive is not plugged in.")
    env = restic_env()
    state = load(STATE, {})
    state.update({"state": "running", "startedAt": int(clock())})
    save(STATE, state)
    command = [RESTIC, "-r", s["repository"], "backup", str(HOME), "--one-file-system", "--tag", "nexusos", "--json"]
    for pattern in EXCLUDES:
        command += ["--exclude", str(HOME / pattern)]
    status, out, err = runner(command, env)
    summary = parse_summary(out)
    if status not in (0, 3):           # 3: some files could not be read; the rest is backed up
        state.update({"state": "failed", "message": "The backup stopped: " + last_error_line(err)})
        save(STATE, state)
        raise BackupError(state["message"])
    runner([RESTIC, "-r", s["repository"], "forget", "--prune", "--tag", "nexusos"] + KEEP, env)
    state.update({"state": "done", "lastBackup": int(clock()), "message": "",
                  "unreadable": status == 3, **({"added": summary.get("data_added", 0), "files": summary.get("total_files_processed", 0)}
                                                if summary else {})})
    save(STATE, state)
    return {"message": "Backed up." + (" Some files could not be read and were skipped." if status == 3 else ""), **state}


def parse_summary(out):
    for line in reversed(out.strip().splitlines()):
        try:
            item = json.loads(line)
        except ValueError:
            continue
        if item.get("message_type") == "summary":
            return item
    return {}


def due(state, settings_present, drive_present, now):
    """Whether the automatic backup should run now."""
    if not settings_present or not drive_present:
        return False
    if state.get("state") == "running" and now - state.get("startedAt", 0) < 6 * 3600:
        return False
    return now - state.get("lastBackup", 0) >= AUTO_AFTER


def auto(runner=run, clock=time.time):
    s = load(SETTINGS, {})
    if s.get("repository") and not Path(s.get("dest", "")).is_dir() and Path("/dev/disk/by-uuid", s.get("uuid") or "-").exists():
        s = mount_drive(s, runner)
    if not due(load(STATE, {}), bool(s.get("repository")), bool(s.get("dest")) and Path(s["dest"]).is_dir(), clock()):
        return {"ran": False}
    return {"ran": True, **backup(runner, clock)}


def snapshots(runner=run):
    s = settings()
    status, out, err = runner([RESTIC, "-r", s["repository"], "snapshots", "--tag", "nexusos", "--json"], restic_env())
    if status != 0:
        raise BackupError("The backups could not be read: " + last_error_line(err))
    items = json.loads(out or "[]")
    return [{"id": i.get("short_id") or i.get("id", "")[:8], "time": i.get("time"), "host": i.get("hostname")}
            for i in sorted(items, key=lambda i: i.get("time", ""), reverse=True)]


SNAPSHOT = re.compile(r"[0-9a-f]{8,64}\Z")


def restore(snapshot_id, path=None, runner=run, now=None):
    if not isinstance(snapshot_id, str) or SNAPSHOT.fullmatch(snapshot_id) is None:
        raise BackupError("Not a backup.")
    if path:
        inside_home = isinstance(path, str) and Path(path).is_absolute() and ".." not in Path(path).parts \
            and (Path(path) == HOME or HOME in Path(path).parents)
        if not inside_home:
            raise BackupError("Only folders in your home folder can be restored.")
    s = settings()
    when = (now or datetime.datetime.now()).strftime("%Y-%m-%d %H.%M.%S")
    target = HOME / "Restored files" / when
    if target.exists():
        raise BackupError("Restored files from this moment are there already; try again.")
    target.mkdir(parents=True)
    command = [RESTIC, "-r", s["repository"], "restore", snapshot_id, "--target", str(target)]
    if path:
        command += ["--include", path]
    status, _, err = runner(command, restic_env())
    if status != 0:
        shutil.rmtree(target, ignore_errors=True)
        raise BackupError("The files could not be restored: " + last_error_line(err))
    # restic recreates the full path (target/home/ada/...): point at the home folder inside.
    inside = target / str(HOME).lstrip("/")
    return {"message": "Restored into " + str(target.relative_to(HOME)) + ". Nothing in your home folder was changed.",
            "folder": str(inside if inside.exists() else target)}


def status():
    s = load(SETTINGS, {})
    state = load(STATE, {})
    dest = s.get("dest")
    return {"setUp": bool(s.get("repository")), "dest": dest, "driveName": Path(dest).name if dest else None,
            "drivePresent": bool(dest) and Path(dest).is_dir(), "state": state.get("state"), "lastBackup": state.get("lastBackup"),
            "message": state.get("message", ""), "unreadable": state.get("unreadable", False)}


def main(argv=None):
    parser = argparse.ArgumentParser(prog="nexus-backup", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("status", "drives", "setup", "run", "auto", "snapshots", "restore"))
    parser.add_argument("--dest")
    parser.add_argument("--snapshot")
    parser.add_argument("--path")
    args = parser.parse_args(argv)
    try:
        if args.command == "status":
            result = status()
        elif args.command == "drives":
            result = {"drives": drives()}
        elif args.command == "setup":
            result = setup(args.dest, sys.stdin.readline().rstrip("\n"))
        elif args.command == "run":
            result = backup()
        elif args.command == "auto":
            result = auto()
        elif args.command == "snapshots":
            result = {"snapshots": snapshots()}
        else:
            result = restore(args.snapshot, args.path)
    except BackupError as error:
        if args.command == "auto":
            try:   # the automatic backup has no window to say it in: a notification does
                run(["notify-send", "--app-name=Backups", "--icon=drive-harddisk", "Backup did not finish", str(error)], timeout=10)
            except (OSError, subprocess.SubprocessError):
                pass
        print(json.dumps({"error": str(error)}))
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
