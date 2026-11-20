#!/usr/bin/env python3
"""Default apps (Settings, Default apps): which app opens web links, mail,
folders, music, videos, pictures, text and PDFs.

Runs as the person. The choice is written to ~/.config/mimeapps.list, the
freedesktop.org file every app and file manager reads, under
[Default Applications]; the rest of the file is kept as it is.

  nexus-default-apps list                 categories, the apps that can open each, the current one (JSON)
  nexus-default-apps set CATEGORY APP     make APP (a desktop file id, "firefox.desktop") the default
"""
import configparser
import json
import os
from pathlib import Path
import re
import sys

HOME = Path(os.environ.get("HOME", "~")).expanduser()
CONFIG = Path(os.environ.get("XDG_CONFIG_HOME", HOME / ".config"))
DATA_HOME = Path(os.environ.get("XDG_DATA_HOME", HOME / ".local/share"))

CATEGORIES = [
    ("browser", "Web browser", ["x-scheme-handler/http", "x-scheme-handler/https", "text/html", "application/xhtml+xml"]),
    ("mail", "Email", ["x-scheme-handler/mailto"]),
    ("files", "Files", ["inode/directory"]),
    ("music", "Music", ["audio/mpeg", "audio/flac", "audio/ogg", "audio/x-wav", "audio/mp4", "audio/x-vorbis+ogg"]),
    ("videos", "Videos", ["video/mp4", "video/x-matroska", "video/webm", "video/quicktime", "video/x-msvideo"]),
    ("pictures", "Pictures", ["image/jpeg", "image/png", "image/gif", "image/webp", "image/heic", "image/tiff"]),
    ("text", "Text files", ["text/plain", "text/markdown"]),
    ("pdf", "PDF documents", ["application/pdf"]),
]
APP_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,200}\.desktop\Z")


class Problem(Exception):
    pass


def application_dirs():
    dirs = [DATA_HOME / "applications"]
    for d in os.environ.get("XDG_DATA_DIRS", "/usr/local/share:/usr/share").split(":"):
        if d:
            dirs.append(Path(d) / "applications")
    seen, out = set(), []
    for d in dirs:
        if d not in seen:
            seen.add(d)
            out.append(d)
    return out


def read_entry(path):
    parser = configparser.RawConfigParser(interpolation=None, strict=False)
    parser.optionxform = str
    try:
        parser.read(path, encoding="utf-8")
    except (configparser.Error, OSError, UnicodeDecodeError):
        return None
    if not parser.has_section("Desktop Entry"):
        return None
    e = parser["Desktop Entry"]
    if e.get("Type", "Application") != "Application" or e.get("NoDisplay") == "true" or e.get("Hidden") == "true":
        return None
    return {"name": e.get("Name", path.stem), "mime": [m for m in e.get("MimeType", "").split(";") if m]}


def installed_apps(dirs=None):
    """Desktop file id -> entry; the first directory with an id wins (the person's own first)."""
    apps = {}
    for d in dirs or application_dirs():
        if not d.is_dir():
            continue
        for path in sorted(d.rglob("*.desktop")):
            app_id = str(path.relative_to(d)).replace("/", "-")
            if app_id in apps:
                continue
            entry = read_entry(path)
            apps[app_id] = entry       # None hides a lower one with the same id, as the spec says
    return {k: v for k, v in apps.items() if v}


def read_mimeapps(path):
    """The [Default Applications] section as {mime: [ids]}, and the file's lines."""
    defaults, lines, section = {}, [], None
    try:
        lines = Path(path).read_text().splitlines()
    except OSError:
        return defaults, lines
    for line in lines:
        s = line.strip()
        if s.startswith("[") and s.endswith("]"):
            section = s[1:-1]
        elif section == "Default Applications" and "=" in s and not s.startswith("#"):
            k, v = s.split("=", 1)
            defaults[k.strip()] = [a for a in v.split(";") if a]
    return defaults, lines


def defaults_from(paths):
    merged = {}
    for p in paths:
        d, _ = read_mimeapps(p)
        for k, v in d.items():
            merged.setdefault(k, v)
    return merged


def mimeapps_paths():
    paths = [CONFIG / "mimeapps.list"]
    for d in os.environ.get("XDG_CONFIG_DIRS", "/etc/xdg").split(":"):
        if d:
            paths.append(Path(d) / "mimeapps.list")
    for d in application_dirs():
        paths.append(d / "mimeapps.list")
    return paths


def listing(apps=None, paths=None):
    apps = installed_apps() if apps is None else apps
    defaults = defaults_from(mimeapps_paths() if paths is None else paths)
    out = []
    for cid, label, mimes in CATEGORIES:
        able = sorted(((i, a["name"]) for i, a in apps.items() if mimes[0] in a["mime"]), key=lambda x: x[1].lower())
        current = next((d for d in defaults.get(mimes[0], []) if d in apps), able[0][0] if able else "")
        out.append({"id": cid, "label": label, "current": current, "apps": [{"id": i, "name": n} for i, n in able]})
    return out


def set_default(category, app_id, apps=None, path=None):
    path = Path(path or CONFIG / "mimeapps.list")
    cat = next((c for c in CATEGORIES if c[0] == category), None)
    if cat is None:
        raise Problem("Not a kind of file.")
    if not isinstance(app_id, str) or not APP_ID.fullmatch(app_id):
        raise Problem("Not an app.")
    apps = installed_apps() if apps is None else apps
    if app_id not in apps:
        raise Problem("That app is not installed.")
    entries = {m: app_id for m in cat[2] if m in apps[app_id]["mime"] or m == cat[2][0]}
    _, lines = read_mimeapps(path)
    out, section, done = [], None, set()
    for line in lines:
        s = line.strip()
        if s.startswith("[") and s.endswith("]"):
            if section == "Default Applications":
                out += [f"{m}={a}" for m, a in entries.items() if m not in done]
                done |= set(entries)
            section = s[1:-1]
        elif section == "Default Applications" and "=" in s and s.split("=", 1)[0].strip() in entries:
            m = s.split("=", 1)[0].strip()
            out.append(f"{m}={entries[m]}")
            done.add(m)
            continue
        out.append(line)
    if section == "Default Applications":
        out += [f"{m}={a}" for m, a in entries.items() if m not in done]
    elif not done:
        if out and out[-1].strip():
            out.append("")
        out += ["[Default Applications]"] + [f"{m}={a}" for m, a in entries.items()]
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text("\n".join(out) + "\n")
    os.replace(tmp, path)
    return {"message": apps[app_id]["name"] + " now opens " + cat[1].lower() + "."}


def main(argv):
    try:
        if argv[:1] == ["list"]:
            result = {"categories": listing()}
        elif len(argv) == 3 and argv[0] == "set":
            result = set_default(argv[1], argv[2])
        else:
            raise Problem("Use: list, or set CATEGORY APP")
    except Problem as p:
        print(json.dumps({"error": str(p)}))
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
