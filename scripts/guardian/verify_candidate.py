#!/usr/bin/env python3
"""Validate a Guardian candidate and write only its strictly allowlisted Nix module."""
import argparse
import json
from pathlib import Path

from candidate import FIELDS, prepare


def extract(candidate, changes):
    if not isinstance(candidate, dict) or set(candidate) != FIELDS:
        raise ValueError("Unexpected candidate manifest fields")
    expected = prepare(changes, candidate.get("baseline"), candidate.get("hostConfig"))
    if candidate != expected:
        raise ValueError("Candidate does not match independently reconstructed proposal")
    return expected["nixModule"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--changes", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    module = extract(json.loads(args.candidate.read_text()), json.loads(args.changes.read_text()))
    if args.output.exists():
        raise ValueError("Refusing to overwrite an existing generated module")
    args.output.write_text(module)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, json.JSONDecodeError) as error:
        raise SystemExit(f"Invalid candidate: {error}")
