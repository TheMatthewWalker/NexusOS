#!/usr/bin/env python3
"""Read-only NixOS generation identity inspection. Never executes deployment commands."""
import argparse
import json
import os
from pathlib import Path

SYSTEM = Path("/run/current-system")
PROFILE = Path("/nix/var/nix/profiles/system")
# The specialisation every staged system carries for its trial boot (guardian.nix):
# the same system, with panic=N boot.panic_on_fail on its boot entry only.
TRIAL_SPECIALISATION = "guardian-trial"

def inspect(current=SYSTEM, profile=PROFILE):
    if not current.is_symlink() or not profile.is_symlink():
        return {"verified": False, "reason": "Missing NixOS generation symlinks"}
    current_target = os.path.realpath(current)
    profile_target = os.path.realpath(profile)
    if not current_target.startswith("/nix/store/") or not profile_target.startswith("/nix/store/"):
        return {"verified": False, "reason": "Generation is not in Nix store"}
    # Booted from the profile's trial-boot entry (a confirmed trial, until the next
    # restart): the same generation, so it is still the baseline.
    trial_target = os.path.realpath(os.path.join(profile_target, "specialisation", TRIAL_SPECIALISATION))
    if current_target != profile_target and current_target != trial_target:
        return {"verified": False, "reason": "Running system differs from system profile"}
    # Paths are observations, not attestations: even identical symlinks do not authorise deployment.
    return {"verified": True, "generationStorePath": profile_target, "profileStorePath": profile_target}

def main():
    parser = argparse.ArgumentParser()
    parser.parse_args()
    observation = inspect()
    print(json.dumps(observation, sort_keys=True))
    if not observation["verified"]:
        raise SystemExit(1)

if __name__ == "__main__":
    main()
