import hashlib
import hmac
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "guardian"))
from candidate import prepare
from broker_policy import canonical, decide

KEY = bytes(range(32))

class BrokerPolicyTests(unittest.TestCase):
    def setUp(self):
        self.changes = [{"option": "services.openssh.enable", "enabled": True}]
        self.candidate = prepare(self.changes, "generation-42")
        self.evidence = {"candidateId":self.candidate["candidateId"],"baseline":"generation-42",
                         "checks":{"evaluate":True,"build":True,"vm-test":True}}
        evidence_sha = hashlib.sha256(canonical(self.evidence)).hexdigest()
        payload = canonical({"candidateId":self.candidate["candidateId"],"evidenceSha256":evidence_sha})
        self.attestation = {"candidateId":self.candidate["candidateId"],"evidenceSha256":evidence_sha,
                            "signature":hmac.new(KEY,payload,hashlib.sha256).hexdigest()}
        self.approval = {"candidateId":self.candidate["candidateId"],"approved":True,"singleUse":True}

    def check(self, **overrides):
        d = dict(candidate=self.candidate,changes=self.changes,evidence=self.evidence,
                 attestation=self.attestation,key=KEY,observed_baseline="generation-42",approval=self.approval)
        d.update(overrides)
        return decide(**d)

    def test_valid_review_decision(self):
        self.assertTrue(self.check())

    def test_forged_or_missing_attestation_fails_closed(self):
        self.assertFalse(self.check(attestation={**self.attestation,"signature":"0"*64}))
        self.assertFalse(self.check(key=b"wrong"))
        self.assertFalse(self.check(attestation={}))

    def test_replay_and_stale_baseline_fail_closed(self):
        self.assertFalse(self.check(observed_baseline="generation-41"))
        self.assertFalse(self.check(approval={**self.approval,"singleUse":False}))
        self.assertFalse(self.check(approval={**self.approval,"candidateId":"another"}))

    def test_failed_build_not_authorised_even_if_signed(self):
        failed = {**self.evidence,"checks":{**self.evidence["checks"],"build":False}}
        evidence_sha = hashlib.sha256(canonical(failed)).hexdigest()
        message = canonical({"candidateId":self.candidate["candidateId"],"evidenceSha256":evidence_sha})
        signed = {"candidateId":self.candidate["candidateId"],"evidenceSha256":evidence_sha,
                  "signature":hmac.new(KEY,message,hashlib.sha256).hexdigest()}
        self.assertFalse(self.check(evidence=failed,attestation=signed))
