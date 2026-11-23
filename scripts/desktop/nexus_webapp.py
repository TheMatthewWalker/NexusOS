#!/usr/bin/env python3
"""Websites as apps (Apps, Web apps; Nexus's add_web_app).

A web app is a desktop file in the person's own applications folder that
opens the site in its own Chromium window, without tabs or an address bar,
so Outlook, Teams or Google Docs sit in the launcher, Alt+Tab and the task
manager like any other app. Sign-ins are shared with Chromium. Nothing is
installed and no password is needed; removing one removes the desktop file.

  nexus-webapp list                 JSON list of web apps
  nexus-webapp add NAME URL         {"id", "name", "url"} or {"error"}
  nexus-webapp remove ID            {"message"} or {"error"}
"""
import json
import os
import re
import sys
from pathlib import Path

PREFIX = "nexus-web-"
# Only http(s) links, and only characters a desktop file's Exec line carries
# without quoting: no spaces, quotes, backslashes, backticks or dollars.
URL = re.compile(r"https?://[A-Za-z0-9.-]+(?::\d{1,5})?(?:/[A-Za-z0-9._~:/?#\[\]@!&()*+,;=%-]*)?")
NAME_MAX = 40


def apps_dir():
    base = os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local/share")
    return Path(base) / "applications"


def slug(name):
    s = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return s[:30] or "site"


def clean_name(name):
    name = re.sub(r"[\x00-\x1f\x7f]", "", str(name or "")).strip()
    return name[:NAME_MAX]


def valid_url(url):
    return bool(URL.fullmatch(str(url or ""))) and len(url) <= 500


def desktop_file(name, url, browser="chromium"):
    # In an Exec line "%" starts a field code, so a literal one is doubled.
    exec_url = url.replace("%", "%%")
    host = re.sub(r"^https?://", "", url).split("/")[0]
    return ("[Desktop Entry]\nType=Application\n"
            f"Name={name}\nComment=Web app: {host}\n"
            f"Exec={browser} --app={exec_url}\n"
            "Icon=web-browser\nTerminal=false\nCategories=Network;WebBrowser;\n"
            f"X-Nexus-WebApp={url}\n")


def read(path):
    fields = {}
    for line in path.read_text(errors="replace").splitlines():
        if "=" in line and not line.startswith("["):
            k, v = line.split("=", 1)
            fields.setdefault(k.strip(), v)
    return fields


def listing(folder=None):
    folder = folder or apps_dir()
    out = []
    for path in sorted(folder.glob(PREFIX + "*.desktop")) if folder.is_dir() else []:
        f = read(path)
        if "X-Nexus-WebApp" in f:
            out.append({"id": path.stem, "name": f.get("Name", path.stem), "url": f["X-Nexus-WebApp"]})
    return out


def add(name, url, folder=None, browser="chromium"):
    folder = folder or apps_dir()
    name = clean_name(name)
    if not name:
        return {"error": "Give the web app a name."}
    if not valid_url(url):
        return {"error": "That is not a web address NexusOS can open as an app (it starts with https://)."}
    for existing in listing(folder):
        if existing["url"] == url:
            return {"id": existing["id"], "name": existing["name"], "url": url, "message": existing["name"] + " is already an app."}
    folder.mkdir(parents=True, exist_ok=True)
    base, n = PREFIX + slug(name), 1
    ident = base
    while (folder / (ident + ".desktop")).exists():
        n += 1
        ident = f"{base}-{n}"
    tmp = folder / (ident + ".desktop.tmp")
    tmp.write_text(desktop_file(name, url, browser))
    tmp.replace(folder / (ident + ".desktop"))
    return {"id": ident, "name": name, "url": url, "message": name + " is in your apps now."}


def remove(ident, folder=None):
    folder = folder or apps_dir()
    if not re.fullmatch(re.escape(PREFIX) + r"[a-z0-9-]{1,40}", str(ident or "")):
        return {"error": "That is not a web app."}
    path = folder / (ident + ".desktop")
    if not path.is_file() or "X-Nexus-WebApp" not in read(path):
        return {"error": "That web app was not found."}
    name = read(path).get("Name", ident)
    path.unlink()
    return {"message": name + " was removed."}


def main(argv):
    browser = os.environ.get("NEXUS_WEBAPP_BROWSER", "chromium")
    if argv[:1] == ["list"]:
        result = listing()
    elif argv[:1] == ["add"] and len(argv) == 3:
        result = add(argv[1], argv[2], browser=browser)
    elif argv[:1] == ["remove"] and len(argv) == 2:
        result = remove(argv[1])
    else:
        result = {"error": "Use: nexus-webapp list | add NAME URL | remove ID"}
    print(json.dumps(result))
    return 1 if isinstance(result, dict) and "error" in result else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
