#!/usr/bin/env python3
"""Fail-closed, nonprivileged Guardian promotion eligibility check."""
import argparse
import json
from pathlib import Path
from verify_candidate import extract

REQUIRED = ("evaluate", "build", "vm-test")

def eligible(candidate, changes, evidence, current_baseline):
    extract(candidate, changes)
    if not isinstance(evidence, dict) or set(evidence) != {"candidateId", "baseline", "checks"}:
        return False
    if current_baseline != candidate["baseline"] or evidence["baseline"] != current_baseline:
        return False
    if evidence["candidateId"] != candidate["candidateId"]:
        return False
    checks = evidence["checks"]
    return isinstance(checks, dict) and set(checks) == set(REQUIRED) and all(checks.get(name) is True for name in REQUIRED)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--changes", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--current-baseline", required=True)
    args = parser.parse_args()
    allowed = eligible(json.loads(args.candidate.read_text()),
                       json.loads(args.changes.read_text()),
                       json.loads(args.evidence.read_text()), args.current_baseline)
    print(json.dumps({"eligibleForReview": allowed, "executed": False}))
    if not allowed: raise SystemExit(1)

if __name__ == "__main__":
    main()
