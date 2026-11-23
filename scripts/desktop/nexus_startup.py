#!/usr/bin/env python3
"""Startup apps (Settings, Default apps): what opens when the person logs in.

These are the person's own autostart entries, ~/.config/autostart/*.desktop:
where Steam, Discord and Flatpak apps (through the background portal) put
themselves when "start at login" is chosen, and where Settings adds one.
Turning one off keeps the file and marks it Hidden, as GNOME and KDE do, so
the app's own setting still shows it was chosen. At login nexus-startup
starts the ones that are on (dex, from desktop.nix's session).

  nexus-startup list               JSON: id, name, on
  nexus-startup set ID on|off
  nexus-startup add APP.desktop    an installed app opens at login
  nexus-startup remove ID          only entries Settings added
"""
import json
import os
import re
import sys
from pathlib import Path

ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9._@+-]{0,120}")
ADDED = "X-Nexus-Added"


def config_home():
    return Path(os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config"))


def autostart_dir():
    return config_home() / "autostart"


def data_dirs():
    home = Path(os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local/share"))
    rest = (os.environ.get("XDG_DATA_DIRS") or "/run/current-system/sw/share").split(":")
    return [home] + [Path(d) for d in rest if d]


def read(path):
    """The [Desktop Entry] group's keys (first value wins), and the file's lines."""
    lines = path.read_text(errors="replace").splitlines()
    fields, group = {}, None
    for line in lines:
        s = line.strip()
        if s.startswith("["):
            group = s
        elif group == "[Desktop Entry]" and "=" in s and not s.startswith("#"):
            k, v = s.split("=", 1)
            fields.setdefault(k.strip(), v.strip())
    return fields, lines


def is_on(fields):
    return fields.get("Hidden", "false").lower() != "true" and \
        fields.get("X-GNOME-Autostart-enabled", "true").lower() != "false"


def listing(folder=None):
    folder = folder or autostart_dir()
    out = []
    for path in sorted(folder.glob("*.desktop")) if folder.is_dir() else []:
        fields, _ = read(path)
        if fields.get("Type", "Application") != "Application":
            continue
        out.append({"id": path.stem, "name": fields.get("Name", path.stem), "on": is_on(fields),
                    "added": fields.get(ADDED) == "true"})
    return out


def set_key(lines, key, value):
    """Set key=value in [Desktop Entry], adding it after the group header if missing."""
    out, group, done = [], None, False
    for line in lines:
        s = line.strip()
        if s.startswith("["):
            if group == "[Desktop Entry]" and not done:
                out.append(f"{key}={value}")
                done = True
            group = s
        elif group == "[Desktop Entry]" and s.split("=", 1)[0].strip() == key:
            if not done:
                out.append(f"{key}={value}")
                done = True
            continue
        out.append(line)
    if group == "[Desktop Entry]" and not done:
        out.append(f"{key}={value}")
    return out


def path_for(ident, folder):
    if not ID.fullmatch(str(ident or "")):
        return None
    p = folder / (ident + ".desktop")
    return p if p.is_file() else None


def set_on(ident, on, folder=None):
    folder = folder or autostart_dir()
    path = path_for(ident, folder)
    if not path:
        return {"error": "That startup app was not found."}
    fields, lines = read(path)
    lines = set_key(set_key(lines, "Hidden", "false" if on else "true"), "X-GNOME-Autostart-enabled", "true" if on else "false")
    tmp = path.with_suffix(".desktop.tmp")
    tmp.write_text("\n".join(lines) + "\n")
    tmp.replace(path)
    name = fields.get("Name", ident)
    return {"message": f"{name} will {'open' if on else 'no longer open'} when you log in."}


def find_app(desktop_id):
    for d in data_dirs():
        p = d / "applications" / desktop_id
        if p.is_file():
            return p
    return None


def add(desktop_id, folder=None):
    folder = folder or autostart_dir()
    if not desktop_id.endswith(".desktop") or not ID.fullmatch(desktop_id[:-8]):
        return {"error": "That is not an app."}
    src = find_app(desktop_id)
    if not src:
        return {"error": "That app is not installed."}
    fields, lines = read(src)
    if fields.get("Type", "Application") != "Application" or not fields.get("Exec"):
        return {"error": "That app cannot be started at login."}
    folder.mkdir(parents=True, exist_ok=True)
    dest = folder / desktop_id
    lines = set_key(set_key(set_key(lines, "Hidden", "false"), "X-GNOME-Autostart-enabled", "true"), ADDED, "true")
    dest.write_text("\n".join(lines) + "\n")
    return {"id": dest.stem, "message": f"{fields.get('Name', dest.stem)} will open when you log in."}


def remove(ident, folder=None):
    folder = folder or autostart_dir()
    path = path_for(ident, folder)
    if not path:
        return {"error": "That startup app was not found."}
    fields, _ = read(path)
    if fields.get(ADDED) != "true":
        return {"error": "That app added itself; turn it off here, or in the app's own settings."}
    path.unlink()
    return {"message": f"{fields.get('Name', ident)} was removed from startup apps."}


def main(argv):
    if argv[:1] == ["list"]:
        result = listing()
    elif argv[:1] == ["set"] and len(argv) == 3 and argv[2] in ("on", "off"):
        result = set_on(argv[1], argv[2] == "on")
    elif argv[:1] == ["add"] and len(argv) == 2:
        result = add(argv[1])
    elif argv[:1] == ["remove"] and len(argv) == 2:
        result = remove(argv[1])
    else:
        result = {"error": "Use: nexus-startup list | set ID on|off | add APP.desktop | remove ID"}
    print(json.dumps(result))
    return 1 if isinstance(result, dict) and "error" in result else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
