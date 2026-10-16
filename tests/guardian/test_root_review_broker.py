import hashlib
import hmac
import os
from pathlib import Path
import socket
import sys
import tempfile
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/"scripts"/"guardian"))
from candidate import prepare
from broker_policy import canonical
from root_review_broker import GuardianReviewBroker, peer_uid

class RootReviewBrokerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name)
        os.chmod(self.path,0o700)
        self.key = self.path/"signing.key"
        self.key.write_bytes(bytes(range(32)))
        os.chmod(self.key,0o600)
        self.uid = os.geteuid()
        self.baseline = "/nix/store/" + "a"*32 + "-nixos-system"
        self.broker = GuardianReviewBroker(self.path,self.key,{self.uid},
            owner=self.uid,observer=lambda:{"verified":True,"generationStorePath":self.baseline})
        self.changes=[{"option":"services.openssh.enable","enabled":True}]
        self.candidate=prepare(self.changes,self.baseline)
        self.evidence={"candidateId":self.candidate["candidateId"],"baseline":self.baseline,
                       "checks":{"evaluate":True,"build":True,"vm-test":True}}
        digest=hashlib.sha256(canonical(self.evidence)).hexdigest()
        self.attestation={"candidateId":self.candidate["candidateId"],"evidenceSha256":digest}
        self.attestation["signature"]=hmac.new(bytes(range(32)),canonical(self.attestation),hashlib.sha256).hexdigest()
        self.request={"candidate":self.candidate,"changes":self.changes,"evidence":self.evidence,
                      "attestation":self.attestation,"nonce":"n"*40}

    def tearDown(self):
        self.temp.cleanup()

    def test_consumes_nonce_once_and_survives_restart(self):
        self.assertTrue(self.broker.register("n"*40,self.candidate["candidateId"],self.uid,110,now=100))
        self.assertTrue(self.broker.review(self.uid,self.request,now=101))
        restored=GuardianReviewBroker(self.path,self.key,{self.uid},
            owner=self.uid,observer=lambda:{"verified":True,"generationStorePath":self.baseline})
        self.assertFalse(restored.review(self.uid,self.request,now=102))

    def test_denies_wrong_uid_and_generation(self):
        self.assertFalse(self.broker.register("n"*40,self.candidate["candidateId"],self.uid+1,110,now=100))
        self.assertTrue(self.broker.register("n"*40,self.candidate["candidateId"],self.uid,110,now=100))
        self.assertFalse(self.broker.review(self.uid+1,self.request,now=101))
        self.broker.observer=lambda:{"verified":True,"generationStorePath":"/nix/store/changed"}
        self.assertFalse(self.broker.review(self.uid,self.request,now=101))

    def test_failed_attestation_consumes_nonce(self):
        self.assertTrue(self.broker.register("n"*40,self.candidate["candidateId"],self.uid,110,now=100))
        bad={**self.request,"attestation":{**self.attestation,"signature":"0"*64}}
        self.assertFalse(self.broker.review(self.uid,bad,now=101))
        self.assertFalse(self.broker.review(self.uid,self.request,now=102))

    def test_rejects_insecure_key(self):
        os.chmod(self.key,0o644)
        with self.assertRaises(PermissionError):
            GuardianReviewBroker(self.path,self.key,{self.uid},owner=self.uid)

    def test_linux_peer_user_credentials(self):
        if not sys.platform.startswith("linux"): return
        sockpath=str(self.path/"test.sock")
        server=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
        server.bind(sockpath)
        server.listen(1)
        try:
            client=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
            client.connect(sockpath)
            accepted,_=server.accept()
            try:
                self.assertEqual(self.uid,peer_uid(accepted))
            finally:
                accepted.close()
                client.close()
        finally:
            server.close()
