#!/usr/bin/env python3
"""App permissions (Settings, Privacy and AI): what each Flathub app may reach.

For an app installed from Flathub, what its makers asked for (its metadata)
with the person's own changes on top (`flatpak override --user`), in plain
words, and switches that change them, for this person only:

  network   the internet                  --share / --unshare=network
  files     your home folder or all files --filesystem / --nofilesystem
  devices   cameras, controllers, USB     --device / --nodevice=all
  camera    camera through the portal     permission store: devices/camera
  background  running with no window      permission store: background/background

  nexus-permissions apps                 JSON: id, name of each app
  nexus-permissions show APP             JSON: each permission and whether it is on
  nexus-permissions set APP KIND on|off
  nexus-permissions reset APP            back to what the app asked for

Runs as the person; no password.
"""
import configparser
import json
import re
import subprocess
import sys

APP = re.compile(r"[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,64}){2,6}")
KINDS = ("network", "files", "devices", "camera", "background")
LABELS = {"network": "The internet", "files": "Your files", "devices": "Cameras, controllers and USB devices",
          "camera": "The camera (when it asks)", "background": "Running in the background"}


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def parse_context(text):
    cp = configparser.ConfigParser(interpolation=None, strict=False)
    cp.optionxform = str
    try:
        cp.read_string(text or "")
    except configparser.Error:
        return {}
    ctx = cp["Context"] if cp.has_section("Context") else {}
    return {k: [x for x in ctx.get(k, "").split(";") if x] for k in ("shared", "devices", "filesystems")}


def merged(metadata, overrides):
    """What the app may do: its metadata, then each override ("!x" takes x away)."""
    out = {k: list(v) for k, v in metadata.items()}
    for key, values in overrides.items():
        cur = out.setdefault(key, [])
        for v in values:
            if v.startswith("!"):
                base = v[1:]
                cur[:] = [x for x in cur if x.split(":")[0] != base]
            elif v not in cur:
                cur.append(v)
    return out


FILES = ("home", "host", "host-os", "host-etc")


def state(ctx, portal):
    files = [f.split(":")[0] for f in ctx.get("filesystems", [])]
    return {"network": "network" in ctx.get("shared", []),
            "files": any(f in FILES or f.startswith("~") or f.startswith("/home") for f in files),
            "devices": "all" in ctx.get("devices", []),
            "camera": portal.get(("devices", "camera"), "yes") != "no",
            "background": portal.get(("background", "background"), "yes") != "no"}


def parse_portal(text, app):
    """`flatpak permission-show APP`: Table  Object  App  Permissions  Data."""
    out = {}
    for line in (text or "").splitlines()[1:]:
        f = line.split()
        if len(f) >= 4 and f[2] == app:
            out[(f[0], f[1])] = f[3].split(",")[0]
    return out


def show(app, runner=run):
    if not APP.fullmatch(str(app or "")):
        return {"error": "That is not a Flathub app id."}
    meta = runner(["flatpak", "info", "--show-metadata", app])
    if meta.returncode != 0:
        return {"error": "That app is not installed from Flathub."}
    over = runner(["flatpak", "override", "--user", "--show", app])
    portal = runner(["flatpak", "permission-show", app])
    ctx = merged(parse_context(meta.stdout), parse_context(over.stdout if over.returncode == 0 else ""))
    asked = parse_context(meta.stdout)
    s = state(ctx, parse_portal(portal.stdout if portal.returncode == 0 else "", app))
    wants = state(asked, {})
    return {"app": app, "permissions": [{"kind": k, "label": LABELS[k], "on": s[k],
                                         "asked": wants[k] if k in ("network", "files", "devices") else True} for k in KINDS]}


def files_asked(app, runner):
    meta = runner(["flatpak", "info", "--show-metadata", app])
    asked = [f for f in parse_context(meta.stdout).get("filesystems", []) if f.split(":")[0] in FILES or f.startswith("~")]
    return asked or ["home"]


def set_permission(app, kind, on, runner=run):
    if not APP.fullmatch(str(app or "")):
        return {"error": "That is not a Flathub app id."}
    if kind not in KINDS:
        return {"error": "Unknown permission."}
    if kind == "network":
        cmd = ["flatpak", "override", "--user", ("--share" if on else "--unshare") + "=network", app]
    elif kind == "devices":
        cmd = ["flatpak", "override", "--user", ("--device" if on else "--nodevice") + "=all", app]
    elif kind == "files":
        if on:
            cmd = ["flatpak", "override", "--user"] + ["--filesystem=" + f for f in files_asked(app, runner)] + [app]
        else:
            cmd = ["flatpak", "override", "--user"] + ["--nofilesystem=" + f for f in FILES + ("xdg-download", "xdg-documents")] + [app]
    else:
        table, obj = ("devices", "camera") if kind == "camera" else ("background", "background")
        cmd = ["flatpak", "permission-set", table, obj, app, "yes" if on else "no"]
    r = runner(cmd)
    if r.returncode != 0:
        return {"error": "flatpak could not change it: " + (r.stderr or "").strip()[:200]}
    return {"message": f"{LABELS[kind]}: {'allowed' if on else 'not allowed'}. It applies the next time the app starts."}


def reset(app, runner=run):
    if not APP.fullmatch(str(app or "")):
        return {"error": "That is not a Flathub app id."}
    runner(["flatpak", "override", "--user", "--reset", app])
    runner(["flatpak", "permission-reset", app])
    return {"message": "Back to what the app asked for. It applies the next time the app starts."}


def apps(runner=run):
    r = runner(["flatpak", "list", "--app", "--columns=application,name"])
    out = []
    for line in (r.stdout or "").splitlines():
        f = line.split("\t")
        if f and APP.fullmatch(f[0]):
            out.append({"id": f[0], "name": f[1] if len(f) > 1 and f[1] else f[0]})
    return sorted({a["id"]: a for a in out}.values(), key=lambda a: a["name"].lower())


def main(argv):
    if argv[:1] == ["apps"]:
        result = apps()
    elif argv[:1] == ["show"] and len(argv) == 2:
        result = show(argv[1])
    elif argv[:1] == ["set"] and len(argv) == 4 and argv[3] in ("on", "off"):
        result = set_permission(argv[1], argv[2], argv[3] == "on")
    elif argv[:1] == ["reset"] and len(argv) == 2:
        result = reset(argv[1])
    else:
        result = {"error": "Use: nexus-permissions apps | show APP | set APP KIND on|off | reset APP"}
    print(json.dumps(result))
    return 1 if isinstance(result, dict) and "error" in result else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
