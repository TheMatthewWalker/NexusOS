#!/usr/bin/env python3
"""Run Guardian as an installed NexusOS has it, for Core's tests (never for production).

The real broker, the real local builder and the real staging service, each on
its own socket in <dir>, owned by the test UID. Only what needs root or a
machine is faked: the observed system, the store check, nix (the builder's
runner answers like a working machine) and the bootloader step (staging
claims the decision once and writes the same log). Desktop approval is the
real desktop_approve.approve(), run when a file <dir>/approve/<candidateId>
holding the build hash appears, standing in for polkit after the password
(pkexec ... <candidateId> <buildHash>). Prints READY once all
three are listening.

  --failing-build   the builder's build step fails
"""
import argparse
import json
import os
from pathlib import Path
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "guardian"))
from attestation import load_trusted_keys  # noqa: E402
from desktop_approve import approve  # noqa: E402
from local_builder import LocalBuilder, init_keys, load_private_key  # noqa: E402
from root_review_broker import GuardianBroker, serve  # noqa: E402
from stage_executor import record  # noqa: E402
from stage_service import StageService  # noqa: E402

BASELINE = "/nix/store/" + "1" * 32 + "-nixos-system-lab-26.05"
TOPLEVEL = "/nix/store/" + "3" * 32 + "-nixos-system-lab-26.05"
DRV = "/nix/store/" + "4" * 32 + "-nixos-system-lab-26.05.drv"
VM_DRV = "/nix/store/" + "8" * 32 + "-nexus-guardian-vm-test.drv"
HOST = "path:/etc/nixos#nixosConfigurations.lab"

parser = argparse.ArgumentParser()
parser.add_argument("--dir", type=Path, required=True)
parser.add_argument("--failing-build", action="store_true")
args = parser.parse_args()
uid = os.geteuid()


def observer():
    return {"verified": True, "generationStorePath": BASELINE, "profileStorePath": BASELINE}


def nix(command, timeout):
    if "eval" in command:
        return 0, json.dumps({"toplevel": DRV, "vmTest": VM_DRV,
                              "before": {"options": {"services.printing.enable": False}, "packages": {}}}), ""
    if command[-1] == DRV + "^out":
        return (1, "", "error: simulated") if args.failing_build else (0, TOPLEVEL + "\n", "")
    return 0, "", ""


builder_dir = args.dir / "builder"
init_keys(builder_dir)
state = args.dir / "state"
state.mkdir(mode=0o700)
broker = GuardianBroker(state, builder_dir / "trusted-keys", {uid}, HOST, owner=uid, observer=observer,
                        store_checker=lambda _: True)
builder = LocalBuilder(builder_dir, load_private_key(builder_dir, owner=uid), {uid}, HOST, observer=observer,
                       runner=nix, owner=uid)
assert load_trusted_keys(builder_dir / "trusted-keys", owner=uid)


def stager(broker, decision, **_):
    toplevel = broker.claim_for_staging(decision)
    record(broker.state_dir, {"event": "staged", "decision": decision, "toplevel": toplevel, "baseline": BASELINE})
    return {"toplevel": toplevel, "baseline": BASELINE}


stage = StageService(broker, bootloader="systemd-boot", stager=stager, pre_stage=lambda: None)


def approvals():
    folder = args.dir / "approve"
    folder.mkdir()
    while True:
        for request in folder.iterdir():
            build = request.read_text().strip()
            request.unlink()
            try:
                approve(broker, request.name, build, uid)
            except Exception as error:  # Refused, as the helper would be; the review then finds no approval.
                print(f"approval refused: {error}", file=sys.stderr, flush=True)
        time.sleep(0.05)


readies = []
for service, name in ((broker, "broker"), (builder, "builder"), (stage, "stage")):
    ready = threading.Event()
    readies.append(ready)
    threading.Thread(target=serve, args=(service, args.dir / f"{name}.sock"), kwargs={"ready": ready}, daemon=True).start()
threading.Thread(target=approvals, daemon=True).start()
for ready in readies:
    ready.wait()
print("READY", flush=True)
threading.Event().wait()
