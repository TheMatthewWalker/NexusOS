#!/usr/bin/env python3
"""Locate NixOS generations in a generated grub.cfg by their exact init= store path.

GRUB selects entries by index path ("2" or "1>3" for an entry inside a
submenu). Indices are stable until the next bootloader install, so callers
resolve them immediately after the bootloader is installed.
"""
import re

ENTRY = re.compile(r"""^\s*(menuentry|submenu)\s+(['"])(.*?)\2.*\{\s*$""")
LINUX = re.compile(r"^\s*linux(?:16|efi)?\s+")


def parse(grub_cfg):
    """Return [(path, label, init_paths)] for every menuentry, path as a tuple of indices."""
    roots, active, depth = [], [], 0
    for line in grub_cfg.splitlines():
        match = ENTRY.match(line)
        if match:
            siblings = roots if not active else active[-1]["children"]
            node = {"kind": match.group(1), "label": match.group(3), "index": len(siblings), "depth": depth,
                    "children": [], "init": []}
            siblings.append(node)
            active.append(node)
        if active and LINUX.match(line):
            active[-1]["init"].extend(arg[5:] for arg in line.split() if arg.startswith("init="))
        depth += line.count("{") - line.count("}")
        while active and depth <= active[-1]["depth"]:
            active.pop()

    entries = []

    def walk(nodes, prefix):
        for node in nodes:
            path = prefix + (node["index"],)
            if node["kind"] == "menuentry":
                entries.append((path, node["label"], node["init"]))
            walk(node["children"], path)
    walk(roots, ())
    return entries


def entry_for(grub_cfg, toplevel, *, prefer_submenu=False):
    """GRUB index path ("1>0") of an entry booting toplevel's init, or None."""
    init = toplevel.rstrip("/") + "/init"
    matches = [path for path, _, inits in parse(grub_cfg) if init in inits]
    if not matches:
        return None
    # The top-level "default" entry follows the system profile; a submenu entry
    # names one generation explicitly.
    ordered = sorted(matches, key=len, reverse=prefer_submenu)
    return ">".join(str(index) for index in ordered[0])
