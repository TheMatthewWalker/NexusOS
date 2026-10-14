import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "guardian"))
from candidate import prepare
from verify_candidate import extract

class CandidateVerificationTests(unittest.TestCase):
    def setUp(self):
        self.changes = [{"option":"services.openssh.enable","enabled":True}]
        self.candidate = prepare(self.changes, "generation-42")

    def test_extract_verified_module(self):
        self.assertIn("services.openssh.enable = true;", extract(self.candidate,self.changes))

    def test_reject_modification(self):
        candidate = {**self.candidate, "nixModule": "{ services.openssh.enable = false; }"}
        with self.assertRaises(ValueError): extract(candidate,self.changes)

    def test_reject_forged_authorization(self):
        candidate = {**self.candidate, "activation": "approved"}
        with self.assertRaises(ValueError): extract(candidate,self.changes)
        candidate = {**self.candidate, "extra": "arbitrary"}
        with self.assertRaises(ValueError): extract(candidate,self.changes)

    def test_reject_baseline_mismatch(self):
        candidate = {**self.candidate, "baseline": "generation-43"}
        with self.assertRaises(ValueError): extract(candidate,self.changes)
