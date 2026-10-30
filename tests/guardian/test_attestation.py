import base64
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

from support import BASELINE, CHANGES, HOST, ROOT, TOPLEVEL, statement
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from attestation import key_id, load_trusted_keys, public_raw, sign, verify
from candidate import prepare


class AttestationTests(unittest.TestCase):
    def setUp(self):
        self.key = Ed25519PrivateKey.generate()
        self.trusted = {key_id(public_raw(self.key)): public_raw(self.key)}
        self.candidate = prepare(CHANGES, BASELINE, HOST)

    def test_round_trip(self):
        verified = verify(sign(statement(self.candidate), self.key), self.trusted)
        self.assertEqual(TOPLEVEL, verified["toplevel"])

    def test_tampering_any_field_breaks_signature(self):
        signed = sign(statement(self.candidate), self.key)
        for field, value in (("toplevel", "/nix/store/" + "9" * 32 + "-nixos-system-evil"),
                             ("checks", {"evaluate": True, "build": True, "vm-test": True, }),
                             ("candidateId", "f" * 24), ("builder", "someone-else")):
            forged = json.loads(json.dumps(signed))
            forged["statement"][field] = value
            if forged["statement"] == signed["statement"]:
                continue
            self.assertIsNone(verify(forged, self.trusted), field)

    def test_todays_values_are_optional_signed_and_strictly_typed(self):
        before = {"options": {"services.printing.enable": False}, "packages": {"hello": None}}
        self.assertEqual(before, verify(sign(statement(self.candidate, before=before), self.key), self.trusted)["before"])
        for bad in ({"options": {}}, {"options": {"x": "yes"}, "packages": {}}, {"options": [], "packages": {}},
                    {"options": {}, "packages": {}, "extra": {}}):
            with self.subTest(before=bad), self.assertRaises(ValueError):
                sign(statement(self.candidate, before=bad), self.key)
        with self.assertRaises(ValueError):
            sign(statement(self.candidate, notes="x"), self.key)

    def test_failed_check_signature_is_valid_but_reports_failure(self):
        verified = verify(sign(statement(self.candidate, checks={"evaluate": True, "build": False, "vm-test": True}), self.key), self.trusted)
        self.assertFalse(verified["checks"]["build"])

    def test_untrusted_or_unknown_key_rejected(self):
        other = Ed25519PrivateKey.generate()
        self.assertIsNone(verify(sign(statement(self.candidate), other), self.trusted))
        signed = sign(statement(self.candidate), self.key)
        signed["keyId"] = "0" * 16
        self.assertIsNone(verify(signed, self.trusted))

    def test_verifier_cannot_forge_with_public_key(self):
        # The broker only ever holds public bytes; they are not a usable signing key.
        with self.assertRaises(ValueError):
            Ed25519PrivateKey.from_private_bytes(public_raw(self.key)[:31])

    def test_stale_or_future_statements_rejected(self):
        now = int(time.time())
        self.assertIsNone(verify(sign(statement(self.candidate, issuedAt=now - 8 * 24 * 3600), self.key), self.trusted, now=now))
        self.assertIsNone(verify(sign(statement(self.candidate, issuedAt=now + 3600), self.key), self.trusted, now=now))

    def test_malformed_attestations_rejected(self):
        signed = sign(statement(self.candidate), self.key)
        for bad in (None, [], {}, {**signed, "extra": 1}, {**signed, "signature": "not base64!"},
                    {**signed, "signature": 5}, {**signed, "statement": {**signed["statement"], "drvPath": "/tmp/x.drv"}}):
            self.assertIsNone(verify(bad, self.trusted))

    def test_statement_requires_store_paths(self):
        with self.assertRaises(ValueError):
            sign(statement(self.candidate, toplevel="/run/current-system"), self.key)

    def test_trusted_key_file_must_not_be_writable_by_others(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "keys"
            path.write_text(base64.b64encode(public_raw(self.key)).decode() + "\n")
            os.chmod(path, 0o644)
            self.assertEqual(self.trusted, load_trusted_keys(path, owner=os.geteuid()))
            os.chmod(path, 0o666)
            with self.assertRaises(PermissionError):
                load_trusted_keys(path, owner=os.geteuid())

    def test_cli_keygen_and_sign(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            script = ROOT / "scripts" / "guardian" / "attestation.py"
            public = subprocess.run([sys.executable, script, "keygen", "--private-key", work / "builder.key"],
                                    check=True, capture_output=True, text=True).stdout.strip()
            self.assertEqual(0o600, (work / "builder.key").stat().st_mode & 0o777)
            (work / "candidate.json").write_text(json.dumps(self.candidate))
            out = subprocess.run([sys.executable, script, "sign", "--private-key", work / "builder.key",
                                  "--candidate", work / "candidate.json", "--toplevel", TOPLEVEL,
                                  "--drv-path", "/nix/store/" + "4" * 32 + "-x.drv", "--builder", "ci",
                                  "--evaluate", "pass", "--build", "pass", "--vm-test", "pass"],
                                 check=True, capture_output=True, text=True).stdout
            raw = base64.b64decode(public)
            self.assertIsNotNone(verify(json.loads(out), {key_id(raw): raw}))
            second = subprocess.run([sys.executable, script, "keygen", "--private-key", work / "builder.key"],
                                    capture_output=True, text=True)
            self.assertNotEqual(0, second.returncode)  # Never overwrites a key.


if __name__ == "__main__":
    unittest.main()
