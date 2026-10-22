"""Shared fixtures for Guardian tests: keys, observed baselines and brokers in temp dirs."""
import base64
import json
import os
from pathlib import Path
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "guardian"))

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402

from attestation import public_raw, sign  # noqa: E402
from root_review_broker import GuardianBroker  # noqa: E402

BASELINE = "/nix/store/" + "1" * 32 + "-nixos-system-lab-26.05"
OTHER_BASELINE = "/nix/store/" + "2" * 32 + "-nixos-system-lab-26.05"
TOPLEVEL = "/nix/store/" + "3" * 32 + "-nixos-system-lab-26.05"
DRV = "/nix/store/" + "4" * 32 + "-nixos-system-lab-26.05.drv"
HOST = "path:/etc/nixos#nixosConfigurations.lab"
CHANGES = [{"option": "services.printing.enable", "enabled": True}]


class Observer:
    def __init__(self, baseline=BASELINE, verified=True):
        self.baseline, self.verified = baseline, verified

    def __call__(self):
        if not self.verified:
            return {"verified": False, "reason": "test"}
        return {"verified": True, "generationStorePath": self.baseline, "profileStorePath": self.baseline}


def statement(candidate, **overrides):
    value = {"attestationVersion": 1, "candidateId": candidate["candidateId"], "baseline": candidate["baseline"],
             "toplevel": TOPLEVEL, "drvPath": DRV, "builder": "test-ci",
             "checks": {"evaluate": True, "build": True, "vm-test": True}, "issuedAt": int(time.time())}
    value.update(overrides)
    return value


class BrokerFixture:
    """A broker owned by the test UID, with one trusted builder key."""

    def __init__(self, allowed=None, observer=None, store_ok=True):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = Path(self.temp.name)
        self.state = self.dir / "state"
        self.state.mkdir(mode=0o700)
        self.uid = os.geteuid()
        self.key = Ed25519PrivateKey.generate()
        self.keys = self.dir / "trusted-keys"
        self.keys.write_text("# builder keys\n" + base64.b64encode(public_raw(self.key)).decode() + "\n")
        os.chmod(self.keys, 0o644)
        self.observer = observer or Observer()
        self.store_ok = store_ok
        self.broker = GuardianBroker(self.state, self.keys, allowed if allowed is not None else {self.uid}, HOST,
                                     owner=self.uid, observer=self.observer,
                                     store_checker=lambda _: self.store_ok)

    def attest(self, candidate, key=None, **overrides):
        return sign(statement(candidate, **overrides), key or self.key)

    def submitted(self, changes=CHANGES):
        return self.broker.submit(self.uid, {"description": "Enable printing", "changes": changes})["candidate"]

    def close(self):
        self.temp.cleanup()


def dumps(value):
    return json.dumps(value, sort_keys=True)
