#!/usr/bin/env python3
"""Prepare a deterministic, non-executing Guardian NixOS candidate (ADR-002)."""
import argparse
import json
from pathlib import Path

from guardian_schema import candidate_id, plan_id, render, require_baseline, require_host_config, risk

OPERATIONS = ["evaluate", "build", "vm-test"]
FIELDS = {"schemaVersion", "candidateId", "planId", "baseline", "hostConfig", "nixModule",
          "risk", "operations", "activation", "rollbackTarget"}


def prepare(changes, baseline, host_config):
    module = render(changes)
    require_baseline(baseline)
    require_host_config(host_config)
    return {
        "schemaVersion": 1,
        "candidateId": candidate_id(baseline, host_config, module),
        "planId": plan_id(module),
        "baseline": baseline,
        "hostConfig": host_config,
        "nixModule": module,
        "risk": risk(changes),
        "operations": list(OPERATIONS),
        # Activation of the running system is never permitted; staging for next
        # boot is a separate, broker-authorised executor step.
        "activation": "prohibited",
        "rollbackTarget": baseline,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--changes", required=True, type=Path)
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--host-config", required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    candidate = prepare(json.loads(args.changes.read_text()), args.baseline, args.host_config)
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
        raise SystemExit(f"Guardian candidate rejected: {exc}")
