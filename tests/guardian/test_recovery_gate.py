import sys
from pathlib import Path
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "guardian"))
from candidate import prepare
from recovery_gate import eligible

class RecoveryGateTests(unittest.TestCase):
    def setUp(self):
        self.changes = [{"option":"services.openssh.enable","enabled":True}]
        self.candidate = prepare(self.changes,"generation-42")
        self.evidence = {"candidateId":self.candidate["candidateId"],"baseline":"generation-42",
                         "checks":{"evaluate":True,"build":True,"vm-test":True}}

    def test_all_verified_allows_review_only(self):
        self.assertTrue(eligible(self.candidate,self.changes,self.evidence,"generation-42"))

    def test_failed_candidate_cannot_advance(self):
        for name in ("evaluate","build","vm-test"):
            failed = {**self.evidence, "checks":{**self.evidence["checks"],name:False}}
            self.assertFalse(eligible(self.candidate,self.changes,failed,"generation-42"))

    def test_stale_baseline_and_replayed_evidence_rejected(self):
        self.assertFalse(eligible(self.candidate,self.changes,self.evidence,"generation-43"))
        self.assertFalse(eligible(self.candidate,self.changes,{**self.evidence,"candidateId":"forged"},"generation-42"))
        self.assertFalse(eligible(self.candidate,self.changes,{**self.evidence,"checks":{"evaluate":True}},"generation-42"))

    def test_unexpected_checks_fail_closed(self):
        forged = {**self.evidence,"checks":{**self.evidence["checks"],"override":True}}
        self.assertFalse(eligible(self.candidate,self.changes,forged,"generation-42"))
