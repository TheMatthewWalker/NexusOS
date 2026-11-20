#!/usr/bin/env python3
"""Accounts and passwords for Settings, Accounts (root, through pkexec).

  nexus-account password   {current, new}            the caller's own password, and the disk's
  nexus-account disk       {disk, new}               the disk's password only (it differed from the login one)
  nexus-account add        {login, fullName, password, admin}
  nexus-account remove     {login, keepFiles}
  nexus-account list                                  (no root needed)

Secrets come as one JSON object on standard input, never as arguments. Polkit
decides who may run which (nix/modules/accounts.nix): changing one's own
password asks for that person's password; adding and removing accounts asks
for an administrator's. The caller is PKEXEC_UID: a password change is only
ever for the caller.

The disk (LUKS2, unlocked at start-up with the person's password) keeps its
own copy of the password. A change adds the new password to the disk first,
then changes the login password, and only then removes the old one from the
disk; if a step fails, what was done is undone, so the disk always opens with
one of the two and the person is told which.
"""
import json
import os
import pwd
import re
import subprocess
import sys
import tempfile

LOGIN = re.compile(r"[a-z_][a-z0-9_-]{0,31}\Z")
RESERVED = {"root", "nixos", "nobody", "nexus-guardian", "greeter"}
MIN_PASSWORD = 10
ROOT_MAPPER = "nexusos-root"
GROUPS = ["networkmanager", "video", "audio", "scanner", "lp"]


class AccountError(Exception):
    """Refused or failed; the message is for the person."""


def run(command, input_text=None):
    result = subprocess.run(command, input=input_text, capture_output=True, text=True, check=False)
    return result.returncode, result.stdout, result.stderr


def check_password(password):
    if not isinstance(password, str) or len(password) < MIN_PASSWORD:
        raise AccountError(f"Use a password of at least {MIN_PASSWORD} characters: it also approves system changes.")
    if "\n" in password or "\0" in password:
        raise AccountError("A password cannot contain a line break.")
    return password


def set_login_password(login, password, runner=run):
    status, _, err = runner(["chpasswd"], f"{login}:{password}\n")
    if status != 0:
        raise AccountError("The password could not be changed: " + (err.strip().splitlines() or ["chpasswd failed"])[-1])


# --- the disk ------------------------------------------------------------------

def disk_device(runner=run):
    """The encrypted partition the system starts from, or None (not encrypted)."""
    status, out, _ = runner(["cryptsetup", "status", ROOT_MAPPER])
    if status != 0:
        return None
    for line in out.splitlines():
        key, _, value = line.strip().partition(":")
        if key == "device" and value.strip().startswith("/dev/"):
            return value.strip()
    return None


class KeyFiles:
    """Passwords for cryptsetup in a private folder on /run (memory), removed afterwards."""
    def __init__(self, *secrets):
        self.dir = tempfile.mkdtemp(prefix="nexus-account-", dir="/run" if os.path.isdir("/run") and os.access("/run", os.W_OK) else None)
        os.chmod(self.dir, 0o700)
        self.paths = []
        for index, secret in enumerate(secrets):
            path = os.path.join(self.dir, f"k{index}")
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "w") as stream:
                stream.write(secret)            # no newline: cryptsetup takes the file's bytes
            self.paths.append(path)

    def __enter__(self):
        return self.paths

    def __exit__(self, *_):
        for path in self.paths:
            try:
                with open(path, "r+b") as stream:
                    stream.write(b"\0" * os.path.getsize(path))
            except OSError:
                pass
            try:
                os.unlink(path)
            except OSError:
                pass
        os.rmdir(self.dir)


def disk_opens_with(device, password, runner=run):
    with KeyFiles(password) as (key,):
        return runner(["cryptsetup", "open", "--test-passphrase", "--key-file", key, device])[0] == 0


def disk_add(device, existing, new, runner=run):
    with KeyFiles(existing, new) as (old_key, new_key):
        status, _, err = runner(["cryptsetup", "luksAddKey", "--key-file", old_key, device, new_key])
    if status != 0:
        raise AccountError("The disk's password could not be changed: " + (err.strip().splitlines() or ["luksAddKey failed"])[-1])


def disk_remove(device, password, runner=run):
    with KeyFiles(password) as (key,):
        return runner(["cryptsetup", "luksRemoveKey", "--key-file", key, device])[0] == 0


def change_password(login, current, new, runner=run):
    """The login password, and the disk's when it was the same as the login one."""
    check_password(new)
    if new == current:
        raise AccountError("The new password is the same as the current one.")
    device = disk_device(runner)
    disk_follows = device is not None and disk_opens_with(device, current, runner)
    if disk_follows:
        disk_add(device, current, new, runner)
    try:
        set_login_password(login, new, runner)
    except AccountError:
        if disk_follows:
            disk_remove(device, new, runner)        # back as it was: the disk opens with the current password
        raise
    if disk_follows and not disk_remove(device, current, runner):
        return {"changed": True, "disk": "both",
                "message": "Your password is changed. The disk still also opens with the old one; Settings will try removing it next time."}
    if device is None:
        return {"changed": True, "disk": "none", "message": "Your password is changed."}
    if disk_follows:
        return {"changed": True, "disk": "changed", "message": "Your password is changed, for logging in and for unlocking the disk at start-up."}
    return {"changed": True, "disk": "different",
            "message": "Your login password is changed. The disk has a different password, so it was not changed: "
                       "enter the disk's password to change it too."}


def change_disk_password(disk_password, new, runner=run):
    check_password(new)
    device = disk_device(runner)
    if device is None:
        raise AccountError("This computer's disk is not encrypted.")
    if not disk_opens_with(device, disk_password, runner):
        raise AccountError("That is not the disk's password.")
    if disk_password == new:
        return {"changed": False, "disk": "changed", "message": "The disk already opens with that password."}
    disk_add(device, disk_password, new, runner)
    disk_remove(device, disk_password, runner)
    return {"changed": True, "disk": "changed", "message": "The disk now unlocks with your new password."}


# --- accounts ------------------------------------------------------------------

def people():
    """Accounts people log in to: UID 1000 and up, with a login shell."""
    out = []
    for entry in pwd.getpwall():
        if entry.pw_uid < 1000 or entry.pw_uid >= 60000 or entry.pw_shell.endswith(("nologin", "false")):
            continue
        out.append({"login": entry.pw_name, "fullName": entry.pw_gecos.split(",")[0], "uid": entry.pw_uid,
                    "home": entry.pw_dir, "admin": is_admin(entry.pw_name)})
    return sorted(out, key=lambda p: p["uid"])


def is_admin(login):
    import grp
    try:
        return login in grp.getgrnam("wheel").gr_mem
    except KeyError:
        return False


def check_login(login):
    if not isinstance(login, str) or LOGIN.fullmatch(login) is None:
        raise AccountError("Login names use lower case letters, digits, - and _, and start with a letter.")
    if login in RESERVED:
        raise AccountError(f"{login} is reserved; pick another name.")
    return login


def add_account(login, full_name, password, admin, runner=run):
    check_login(login)
    check_password(password)
    if not isinstance(full_name, str) or len(full_name) > 100 or any(ord(c) < 32 for c in full_name) or ":" in full_name or "," in full_name:
        raise AccountError("Use one line for the full name, without : or ,.")
    try:
        pwd.getpwnam(login)
        raise AccountError(f"There is already an account called {login}.")
    except KeyError:
        pass
    groups = GROUPS + (["wheel"] if admin else [])
    status, _, err = runner(["useradd", "--create-home", "--comment", full_name, "--groups", ",".join(groups), login])
    if status != 0:
        raise AccountError("The account could not be added: " + (err.strip().splitlines() or ["useradd failed"])[-1])
    try:
        set_login_password(login, password, runner)
    except AccountError:
        runner(["userdel", "--remove", login])
        raise
    return {"added": login, "message": f"{full_name or login} can now log in."}


def remove_account(login, keep_files, caller, protected, runner=run):
    check_login(login)
    if login == caller:
        raise AccountError("You cannot remove the account you are using.")
    if login in protected:
        raise AccountError(f"{login} is this computer's first account; it stays.")
    try:
        entry = pwd.getpwnam(login)
    except KeyError:
        raise AccountError(f"There is no account called {login}.") from None
    if entry.pw_uid < 1000:
        raise AccountError(f"{login} is a system account.")
    runner(["loginctl", "terminate-user", login])
    status, _, err = runner(["userdel"] + ([] if keep_files else ["--remove"]) + [login])
    if status not in (0, 12):          # 12: the home folder could not be removed
        raise AccountError("The account could not be removed: " + (err.strip().splitlines() or ["userdel failed"])[-1])
    return {"removed": login, "message": f"{login} is removed" + (f"; their files stay in {entry.pw_dir}." if keep_files else ", with their files.")}


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if not argv or argv[0] not in ("password", "disk", "add", "remove", "list"):
        print(__doc__, file=sys.stderr)
        return 2
    command = argv[0]
    try:
        if command == "list":
            result = {"accounts": people()}
        else:
            if os.geteuid() != 0:
                raise AccountError("This needs to run through pkexec.")
            try:
                caller = pwd.getpwuid(int(os.environ["PKEXEC_UID"])).pw_name
            except (KeyError, ValueError):
                raise AccountError("Run from Settings (pkexec) only.") from None
            request = json.loads(sys.stdin.read(65536) or "{}")
            if not isinstance(request, dict):
                raise AccountError("Malformed request")
            if command == "password":
                result = change_password(caller, str(request.get("current", "")), request.get("new"))
            elif command == "disk":
                result = change_disk_password(str(request.get("disk", "")), request.get("new"))
            elif command == "add":
                result = add_account(request.get("login"), request.get("fullName", ""), request.get("password"), bool(request.get("admin")))
            else:
                protected = [p for p in os.environ.get("NEXUS_FIRST_ACCOUNT", "").split(",") if p]
                result = remove_account(request.get("login"), bool(request.get("keepFiles")), caller, protected)
    except AccountError as error:
        print(json.dumps({"error": str(error)}))
        return 1
    except (ValueError, json.JSONDecodeError):
        print(json.dumps({"error": "Malformed request"}))
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
