#!/usr/bin/env python3
"""Prepare a deterministic, non-executing Guardian NixOS candidate."""
import argparse
import hashlib
import json
from pathlib import Path
from validate import render

def prepare(changes, baseline):
    if not isinstance(baseline, str) or not baseline or len(baseline) > 128:
        raise ValueError("A known-good baseline identifier is required")
    if not all(c.isalnum() or c in ".-_" for c in baseline):
        raise ValueError("Invalid baseline identifier")
    snippet = render(changes)
    identity = hashlib.sha256((baseline + "\n" + snippet).encode()).hexdigest()[:24]
    return {
        "candidateId": identity,
        "baseline": baseline,
        "nixModule": snippet,
        "operations": ["evaluate", "build", "vm-test"],
        "activation": "prohibited",
        "rollbackTarget": baseline
    }

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--changes", required=True, type=Path)
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    candidate = prepare(json.loads(args.changes.read_text()), args.baseline)
    payload = json.dumps(candidate, indent=2, sort_keys=True) + "\n"
    if args.output:
        if args.output.exists():
            raise ValueError("Refusing to overwrite an existing candidate")
        args.output.write_text(payload)
    else:
        print(payload, end="")

if __name__ == "__main__":
    try:
        main()
    except (ValueError, json.JSONDecodeError) as exc:
        parser_message = f"Guardian candidate rejected: {exc}"
        raise SystemExit(parser_message)
