#!/usr/bin/env python3
"""Locate NixOS generations among systemd-boot entries by their exact init= store path.

NixOS writes one file per generation, /boot/loader/entries/nixos-generation-N.conf
(plus -specialisation-X variants), whose `options` line names the generation's
init. An entry's ID is its file name, which is what systemd-boot's default and
one-shot EFI variables name. Reading only: nothing here changes the bootloader.
"""
from pathlib import Path
import re

GENERATION = re.compile(r"nixos-generation-(\d+)(?:-specialisation-(.+))?\.conf\Z")


def entries(entries_dir):
    """Return [(entry_id, generation_number, specialisation, init_paths)]; specialisation is None for plain entries."""
    found = []
    directory = Path(entries_dir)
    if not directory.is_dir():
        return found
    for path in directory.iterdir():
        match = GENERATION.fullmatch(path.name)
        if match is None or not path.is_file():
            continue  # Foreign entries are never chosen.
        inits = []
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            parts = line.split()
            if parts and parts[0] == "options":
                inits.extend(arg[5:] for arg in parts[1:] if arg.startswith("init="))
        found.append((path.name, int(match.group(1)), match.group(2), inits))
    return found


def entry_for(entries_dir, toplevel, specialisation=None):
    """Entry ID booting toplevel's init (the newest such generation), or None.

    Only entries of the named specialisation are considered (plain generation
    entries when it is None), so another specialisation is never chosen.
    """
    init = toplevel.rstrip("/") + "/init"
    matches = [(number, entry_id) for entry_id, number, special, inits in entries(entries_dir)
               if special == specialisation and init in inits]
    return max(matches)[1] if matches else None
