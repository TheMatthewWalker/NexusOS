#!/usr/bin/env python3
"""Run the real Guardian broker for cross-language tests (never for production).

Uses a fake observed baseline and store check, trusts a freshly generated
builder key, and for each submission auto-registers one root approval and
writes <dir>/<candidateId>.attestation.json, standing in for the root console
and CI builder. Prints READY once listening on <dir>/broker.sock.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "guardian"))
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402
from attestation import public_raw, sign  # noqa: E402
from root_review_broker import GuardianBroker, serve  # noqa: E402

BASELINE = "/nix/store/" + "1" * 32 + "-nixos-system-lab-26.05"
TOPLEVEL = "/nix/store/" + "3" * 32 + "-nixos-system-lab-26.05"
DRV = "/nix/store/" + "4" * 32 + "-nixos-system-lab-26.05.drv"

parser = argparse.ArgumentParser()
parser.add_argument("--dir", type=Path, required=True)
parser.add_argument("--allow-uid", type=int, default=os.geteuid())
parser.add_argument("--failing-build", action="store_true")
args = parser.parse_args()

state = args.dir / "state"
state.mkdir(mode=0o700)
key = Ed25519PrivateKey.generate()
keys = args.dir / "trusted-keys"
keys.write_text(base64.b64encode(public_raw(key)).decode() + "\n")
os.chmod(keys, 0o644)


class TestBroker(GuardianBroker):
    def submit(self, uid, payload):
        result = super().submit(uid, payload)
        candidate = result["candidate"]
        self.register_approval(candidate["candidateId"], uid)
        statement = {"attestationVersion": 1, "candidateId": candidate["candidateId"], "baseline": candidate["baseline"],
                     "toplevel": TOPLEVEL, "drvPath": DRV, "builder": "test-harness", "issuedAt": int(time.time()),
                     "checks": {"evaluate": True, "build": not args.failing_build, "vm-test": True}}
        (args.dir / f"{candidate['candidateId']}.attestation.json").write_text(json.dumps(sign(statement, key)))
        return result


broker = TestBroker(state, keys, {args.allow_uid}, "path:/etc/nixos#nixosConfigurations.lab", owner=os.geteuid(),
                    observer=lambda: {"verified": True, "generationStorePath": BASELINE, "profileStorePath": BASELINE},
                    store_checker=lambda _: True)
ready = threading.Event()
threading.Thread(target=lambda: (ready.wait(), print("READY", flush=True)), daemon=True).start()
serve(broker, args.dir / "broker.sock", ready=ready)
