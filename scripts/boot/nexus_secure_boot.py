#!/usr/bin/env python3
"""Secure Boot (Settings, About): where this computer is, and the one root step.

  nexus-secure-boot status   as the person: the firmware's state and the next step (JSON)
  nexus-secure-boot enroll   as root (pkexec nexus-secure-boot-enroll): put this computer's
                             keys in the firmware, with Microsoft's and the firmware's own
                             kept, so Windows and add-in cards still start. Only in Setup Mode.

The steps (nix/modules/secure-boot.nix):
  prepare   nexusos.secureBoot.enable, through Guardian: keys are made on this computer and
            systemd-boot and every kernel are signed with them each time the start-up menu
            is written
  setup     in the firmware settings, clear the Secure Boot keys (Setup Mode)
  enroll    back in NexusOS: this computer's keys go into the firmware
  turn-on   in the firmware settings, switch Secure Boot on
  on        done
"""
import json
import os
from pathlib import Path
import subprocess
import sys

GLOBAL = "8be4df61-93ca-11d2-aa0d-00e098032b8c"
EFIVARS = Path(os.environ.get("NEXUS_EFIVARS", "/sys/firmware/efi/efivars"))
KEYS = Path(os.environ.get("NEXUS_SBCTL_KEYS", "/var/lib/sbctl/keys"))
STATE = Path(os.environ.get("NEXUS_SECURE_BOOT_STATE", "/var/lib/nexus-secure-boot"))
SBCTL = os.environ.get("NEXUS_SBCTL", "sbctl")


def flag(name, efivars=None):
    """A one-byte EFI global variable (SecureBoot, SetupMode): 4 bytes of attributes, then the value."""
    try:
        data = ((efivars or EFIVARS) / f"{name}-{GLOBAL}").read_bytes()
    except OSError:
        return None
    return bool(data[4]) if len(data) >= 5 else None


def status(prepared):
    efi = EFIVARS.is_dir()
    on, setup = flag("SecureBoot"), flag("SetupMode")
    enrolled = (STATE / "enrolled").exists()
    if not efi:
        step = "unsupported"
    elif on:
        step = "on"
    elif not prepared:
        step = "prepare"
    elif setup:
        step = "enroll"
    elif enrolled:
        step = "turn-on"
    else:
        step = "setup"
    return {"efi": efi, "secureBoot": bool(on), "setupMode": bool(setup), "prepared": prepared,
            "keys": KEYS.is_dir(), "enrolled": enrolled, "step": step}


def enroll(prepared, runner=subprocess.run):
    if os.geteuid() != 0:
        return {"error": "Putting keys in the firmware needs an administrator."}
    s = status(prepared)
    if not s["prepared"] or not s["keys"]:
        return {"error": "Secure Boot is not prepared yet: turn it on in Settings first, and restart."}
    if not s["setupMode"]:
        return {"error": "The firmware is not in Setup Mode. Restart into the firmware settings and clear the Secure Boot keys first."}
    r = runner([SBCTL, "enroll-keys", "--microsoft", "--firmware-builtin"], capture_output=True, text=True, check=False)
    if r.returncode != 0:
        last = (r.stderr or r.stdout or "").strip().splitlines()
        return {"error": "The keys could not be put in the firmware: " + (last[-1] if last else "sbctl failed")}
    STATE.mkdir(parents=True, exist_ok=True)
    (STATE / "enrolled").write_text("1\n")
    return {"message": "This computer's keys are in the firmware. Restart into the firmware settings and switch Secure Boot on."}


def main(argv):
    prepared = os.environ.get("NEXUS_SECURE_BOOT_PREPARED") == "1"
    if argv == ["status"]:
        result = status(prepared)
    elif argv == ["enroll"]:
        result = enroll(prepared)
    else:
        result = {"error": "Use: status or enroll"}
    print(json.dumps(result))
    return 1 if "error" in result else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
