#!/usr/bin/env python3
"""Cloud drives as folders (Settings, Accounts, Cloud drives).

OneDrive, Google Drive, Dropbox and Nextcloud appear as a folder in the
home folder (~/OneDrive, as on Windows) and in the file manager's side bar.
rclone does the work: `rclone mount` with a local cache, so files open
quickly and changes upload in the background, started at login by the user
unit nexus-cloud@NAME (nexus-cloud start-all starts each one at login).
Runs as the person; nothing needs a password.

  nexus-cloud list                          JSON: each drive, its folder, whether it is mounted
  nexus-cloud add onedrive|googledrive|dropbox NAME
                                            signs in in the browser, then mounts
  nexus-cloud add-nextcloud NAME URL USER   app password on standard input
  nexus-cloud remove NAME                   unmounts and forgets it (files on the cloud stay)
  nexus-cloud start-all                     at login: mounts every cloud drive

Sign-in tokens stay in rclone's own file, ~/.config/rclone/rclone.conf,
readable only by the person.
"""
import configparser
import json
import os
import re
import subprocess
import sys
from pathlib import Path

KINDS = {"onedrive": ("onedrive", "OneDrive"), "googledrive": ("drive", "Google Drive"),
         "dropbox": ("dropbox", "Dropbox"), "nextcloud": ("webdav", "Nextcloud")}
NAME = re.compile(r"[A-Za-z][A-Za-z0-9_-]{0,29}")
HTTPS = re.compile(r"https://[A-Za-z0-9.-]+(?::\d{1,5})?(?:/[A-Za-z0-9._~/-]*)?")
USER = re.compile(r"[A-Za-z0-9._@+-]{1,100}")
UNIT = "nexus-cloud@{}.service"


def home():
    return Path(os.environ.get("HOME") or Path.home())


def config_path():
    base = os.environ.get("XDG_CONFIG_HOME") or str(home() / ".config")
    return Path(base) / "rclone" / "rclone.conf"


def bookmarks_path():
    base = os.environ.get("XDG_CONFIG_HOME") or str(home() / ".config")
    return Path(base) / "gtk-3.0" / "bookmarks"


def read_config(path=None):
    cp = configparser.ConfigParser(interpolation=None)
    p = path or config_path()
    if p.is_file():
        cp.read(p)
    return cp


def kind_of(section):
    if section.get("nexus_kind") in KINDS:
        return section["nexus_kind"]
    for k, (t, _) in KINDS.items():
        if section.get("type") == t:
            return k
    return None


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def mounted(folder):
    try:
        return os.path.ismount(folder)
    except OSError:
        return False


def listing(path=None):
    cp = read_config(path)
    out = []
    for name in cp.sections():
        kind = kind_of(cp[name])
        if not kind or not NAME.fullmatch(name):
            continue
        folder = home() / name
        out.append({"name": name, "kind": kind, "label": KINDS[kind][1], "folder": str(folder), "mounted": mounted(folder)})
    return out


def check_name(name, path=None):
    if not NAME.fullmatch(str(name or "")):
        return "Use a short name of letters and digits, starting with a letter (it becomes the folder's name)."
    if read_config(path).has_section(name):
        return f"There is already a cloud drive called {name}."
    folder = home() / name
    if folder.exists() and (not folder.is_dir() or any(folder.iterdir())):
        return f"There is already a {name} folder in your home folder with files in it; choose another name."
    return None


def add_bookmark(name, path=None):
    p = path or bookmarks_path()
    line = (home() / name).as_uri() + " " + name
    lines = p.read_text().splitlines() if p.is_file() else []
    if line not in lines:
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text("\n".join(lines + [line]) + "\n")


def drop_bookmark(name, path=None):
    p = path or bookmarks_path()
    if p.is_file():
        uri = (home() / name).as_uri()
        kept = [l for l in p.read_text().splitlines() if l.split(" ")[0] != uri]
        p.write_text("\n".join(kept) + ("\n" if kept else ""))


def start(name, runner=run):
    (home() / name).mkdir(exist_ok=True)
    add_bookmark(name)
    r = runner(["systemctl", "--user", "start", UNIT.format(name)])
    if r.returncode != 0:
        return {"error": f"{name} was signed in to, but its folder could not be started: " + (r.stderr or "").strip()[:200]}
    return {"message": f"{name} is in your home folder and in Files."}


def add(kind, name, runner=run):
    if kind not in KINDS or kind == "nextcloud":
        return {"error": "Choose OneDrive, Google Drive or Dropbox."}
    problem = check_name(name)
    if problem:
        return {"error": problem}
    rtype = KINDS[kind][0]
    # rclone opens the browser to sign in and waits for it (up to its own timeout).
    r = runner(["rclone", "config", "create", name, rtype, "nexus_kind=" + kind])
    if r.returncode != 0 or not read_config().has_section(name):
        runner(["rclone", "config", "delete", name])
        return {"error": "Signing in did not finish. Try again, and allow access in the browser window that opens."}
    return start(name, runner)


def add_nextcloud(name, url, user, app_password, runner=run):
    problem = check_name(name)
    if problem:
        return {"error": problem}
    url = str(url or "").rstrip("/")
    if not HTTPS.fullmatch(url):
        return {"error": "Give the Nextcloud address, starting with https://"}
    if not USER.fullmatch(str(user or "")):
        return {"error": "Give your Nextcloud user name."}
    if not app_password:
        return {"error": "Give an app password (Nextcloud: Settings, Security, Create new app password)."}
    # rclone keeps the password obscured in its file; obscure reads it on standard input, never a command line.
    r = runner(["rclone", "obscure", "-"], input=app_password)
    if r.returncode != 0 or not r.stdout.strip():
        return {"error": "rclone could not store the password."}
    path = config_path()
    cp = read_config(path)
    cp[name] = {"type": "webdav", "url": f"{url}/remote.php/dav/files/{user}", "vendor": "nextcloud",
                "user": user, "pass": r.stdout.strip(), "nexus_kind": "nextcloud"}
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        cp.write(f)
    check = runner(["rclone", "lsd", name + ":", "--max-depth", "1"])
    if check.returncode != 0:
        cp.remove_section(name)
        fd = os.open(path, os.O_WRONLY | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            cp.write(f)
        return {"error": "Nextcloud did not accept that address, user name and app password."}
    return start(name, runner)


def remove(name, runner=run):
    if not NAME.fullmatch(str(name or "")) or not any(d["name"] == name for d in listing()):
        return {"error": "There is no cloud drive by that name."}
    runner(["systemctl", "--user", "stop", UNIT.format(name)])
    runner(["rclone", "config", "delete", name])
    drop_bookmark(name)
    folder = home() / name
    try:
        if folder.is_dir() and not mounted(folder) and not any(folder.iterdir()):
            folder.rmdir()
    except OSError:
        pass
    return {"message": f"{name} was removed from this computer. Its files are still in the cloud."}


def start_all(runner=run):
    for d in listing():
        (home() / d["name"]).mkdir(exist_ok=True)
        runner(["systemctl", "--user", "start", "--no-block", UNIT.format(d["name"])])
    return {"message": "Started."}


def main(argv):
    if argv[:1] == ["list"]:
        result = listing()
    elif argv[:1] == ["add"] and len(argv) == 3:
        result = add(argv[1], argv[2])
    elif argv[:1] == ["add-nextcloud"] and len(argv) == 4:
        result = add_nextcloud(argv[1], argv[2], argv[3], sys.stdin.readline().rstrip("\n"))
    elif argv[:1] == ["remove"] and len(argv) == 2:
        result = remove(argv[1])
    elif argv[:1] == ["start-all"]:
        result = start_all()
    else:
        result = {"error": "Use: nexus-cloud list | add KIND NAME | add-nextcloud NAME URL USER | remove NAME"}
    print(json.dumps(result))
    return 1 if isinstance(result, dict) and "error" in result else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
