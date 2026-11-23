#!/usr/bin/env python3
"""Shared folders (Settings, Network): folders other computers on this
network can open, as Windows' Share tab. Each is the person's own Samba
"usershare" (`net usershare`): no password, no system change once sharing
is on (nexusos.sharing.enable, through Guardian). Shares are read-only:
others can open and copy, never change or delete. Only folders in the
person's home folder can be shared.

  nexus-share list            JSON: name, path of each shared folder
  nexus-share add PATH        share it (its name is the folder's)
  nexus-share remove NAME     stop sharing it
"""
import json
import os
import re
import subprocess
import sys
from pathlib import Path

NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9 _-]{0,39}")


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def home():
    return Path(os.environ.get("HOME") or Path.home()).resolve()


def share_name(path):
    base = re.sub(r"[^A-Za-z0-9 _-]+", "", Path(path).name).strip()[:40]
    return base or "Shared"


def parse_info(text):
    """`net usershare info`: [name] then path=..., comment=..., usershare_acl=..., guest_ok=..."""
    out, cur = [], None
    for line in (text or "").splitlines():
        line = line.strip()
        m = re.fullmatch(r"\[(.+)\]", line)
        if m:
            cur = {"name": m.group(1)}
            out.append(cur)
        elif cur is not None and "=" in line:
            k, v = line.split("=", 1)
            cur[k.strip()] = v.strip()
    return [{"name": s["name"], "path": s.get("path", "")} for s in out]


def listing(runner=run):
    r = runner(["net", "usershare", "info"])
    return parse_info(r.stdout) if r.returncode == 0 else []


def add(path, runner=run):
    try:
        p = Path(path).resolve(strict=True)
    except (OSError, RuntimeError):
        return {"error": "That folder was not found."}
    h = home()
    if not p.is_dir():
        return {"error": "Only folders can be shared."}
    if p == h or h not in p.parents:
        return {"error": "Share a folder inside your home folder (not the whole home folder)."}
    if any(part.startswith(".") for part in p.relative_to(h).parts):
        return {"error": "Hidden folders (settings and keys) cannot be shared."}
    name = share_name(p)
    if not NAME.fullmatch(name):
        return {"error": "That folder's name cannot be used for sharing."}
    if any(s["name"].lower() == name.lower() for s in listing(runner)):
        return {"error": f"A shared folder called {name} already exists."}
    # Everyone may read; guests (no password) are allowed: read-only either way.
    r = runner(["net", "usershare", "add", name, str(p), "Shared from NexusOS", "Everyone:R", "guest_ok=y"])
    if r.returncode != 0:
        msg = (r.stderr or r.stdout or "").strip()
        if "not allowed" in msg or "usershare" in msg and "permission" in msg.lower():
            return {"error": "Sharing is not turned on yet. Turn it on in Settings, Network."}
        return {"error": "The folder could not be shared: " + msg[:200]}
    return {"name": name, "message": f"{name} is shared: others on this network can open and copy it, not change it."}


def remove(name, runner=run):
    if not NAME.fullmatch(str(name or "")) or not any(s["name"] == name for s in listing(runner)):
        return {"error": "That folder is not shared."}
    r = runner(["net", "usershare", "delete", name])
    if r.returncode != 0:
        return {"error": "It could not be stopped: " + (r.stderr or "").strip()[:200]}
    return {"message": f"{name} is no longer shared."}


def main(argv):
    if argv[:1] == ["list"]:
        result = listing()
    elif argv[:1] == ["add"] and len(argv) == 2:
        result = add(argv[1])
    elif argv[:1] == ["remove"] and len(argv) == 2:
        result = remove(argv[1])
    else:
        result = {"error": "Use: nexus-share list | add PATH | remove NAME"}
    print(json.dumps(result))
    return 1 if isinstance(result, dict) and "error" in result else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
