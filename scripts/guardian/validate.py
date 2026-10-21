#!/usr/bin/env python3
"""Validate Guardian's allowlisted NixOS options; never deploys or runs as root."""
import argparse
import json
import pathlib
import subprocess
import sys

from guardian_schema import render  # noqa: F401  (re-exported for callers)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--changes", required=True, type=pathlib.Path)
    parser.add_argument("--parse", action="store_true", help="also parse the module with nix-instantiate")
    args = parser.parse_args()
    snippet = render(json.loads(args.changes.read_text()))
    if not args.parse:
        print(snippet, end="")
        return 0
    # Pure parsing of the generated expression: no evaluation, build or activation.
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
