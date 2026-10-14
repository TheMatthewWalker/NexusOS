#!/usr/bin/env python3
"""Validate Guardian's allowlisted NixOS options, never deploy or run as root."""
import argparse
import json
import pathlib
import re
import subprocess
import sys

ALLOWED = {
    "services.printing.enable",
    "services.openssh.enable",
    "services.flatpak.enable",
    "networking.networkmanager.enable",
}

def render(changes):
    if not isinstance(changes, list) or not 1 <= len(changes) <= 10:
        raise ValueError("Expected 1 to 10 changes")
    values = {}
    for change in changes:
        if not isinstance(change, dict) or set(change) != {"option", "enabled"}:
            raise ValueError("Invalid change shape")
        key, enabled = change["option"], change["enabled"]
        if key not in ALLOWED or type(enabled) is not bool or key in values:
            raise ValueError("Unsupported or duplicate option")
        values[key] = enabled
    return "{\n" + "\n".join(
        f"  {key} = {'true' if value else 'false'};"
        for key, value in sorted(values.items())
    ) + "\n}\n"

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--changes", required=True, type=pathlib.Path)
    parser.add_argument("--flake", type=pathlib.Path)
    parser.add_argument("--system", default="x86_64-linux")
    args = parser.parse_args()
    changes = json.loads(args.changes.read_text())
    snippet = render(changes)
    if not args.flake:
        print(snippet, end="")
        return 0
    # No execution or switching: only pure nix-instantiate parsing of generated expression.
    result = subprocess.run(["nix-instantiate", "--parse", "--expr", snippet],
                            capture_output=True, text=True, timeout=30, check=False)
    if result.returncode:
        print(result.stderr, file=sys.stderr)
    else:
        print("Guardian Nix syntax validated")
    return result.returncode

if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"Guardian validation rejected request: {exc}", file=sys.stderr)
        sys.exit(2)
