import json
import os
import pwd
import tempfile
import unittest
from pathlib import Path

from support import BrokerFixture, HOST
from root_approval_issuer import approve, load_config, require_root_console, resolve_uids
from root_review_broker import Denied


def own_user():
    return pwd.getpwuid(os.geteuid()).pw_name


class ApprovalIssuerTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture()
        self.addCleanup(self.fx.close)
        self.candidate = self.fx.submitted()

    def test_root_console_required(self):
        for uid, stdin, stdout in ((1000, True, True), (0, False, True), (0, True, False)):
            with self.assertRaises(PermissionError):
                require_root_console(uid=uid, stdin_tty=stdin, stdout_tty=stdout)
        require_root_console(uid=0, stdin_tty=True, stdout_tty=True)

    def test_approval_requires_console_check(self):
        def deny():
            raise PermissionError("no console")
        with self.assertRaises(PermissionError):
            approve(self.fx.broker, self.candidate["candidateId"], own_user(), lambda _: "", console_check=deny)

    def test_confirmation_must_name_the_candidate(self):
        with self.assertRaises(PermissionError):
            approve(self.fx.broker, self.candidate["candidateId"], own_user(), lambda _: "APPROVE",
                    console_check=lambda: None)
        self.assertFalse(self.fx.broker.status(self.fx.uid, {"candidateId": self.candidate["candidateId"]})["approvalPending"])

    def test_confirmed_approval_is_registered_once(self):
        approval = approve(self.fx.broker, self.candidate["candidateId"], own_user(),
                           lambda _: "APPROVE " + self.candidate["candidateId"], console_check=lambda: None)
        self.assertEqual(32, len(approval))
        status = self.fx.broker.status(self.fx.uid, {"candidateId": self.candidate["candidateId"]})
        self.assertTrue(status["approvalPending"])
        self.fx.broker.review(self.fx.uid, {"candidateId": self.candidate["candidateId"],
                                            "attestation": self.fx.attest(self.candidate)})
        self.assertFalse(self.fx.broker.status(self.fx.uid, {"candidateId": self.candidate["candidateId"]})["approvalPending"])

    def test_unknown_candidate_cannot_be_approved(self):
        with self.assertRaises(Denied):
            approve(self.fx.broker, "f" * 24, own_user(), lambda _: "APPROVE " + "f" * 24, console_check=lambda: None)

    def test_root_is_never_a_broker_client(self):
        with self.assertRaises(ValueError):
            resolve_uids(["root"])

    def test_config_validation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "config.json"
            config = {"stateDir": "/var/lib/nexus-guardian", "socket": "/run/nexus-guardian/review.sock",
                      "socketGroup": "nexus-guardian", "allowedUsers": ["alice"], "hostConfig": HOST,
                      "trustedKeys": "/etc/nexus-guardian/trusted-keys"}
            path.write_text(json.dumps(config))
            os.chmod(path, 0o644)
            self.assertEqual(config, load_config(path, owner=os.geteuid()))
            path.write_text(json.dumps({**config, "allowRoot": True}))
            with self.assertRaises(ValueError):
                load_config(path, owner=os.geteuid())
            os.chmod(path, 0o666)
            with self.assertRaises(PermissionError):
                load_config(path, owner=os.geteuid())


if __name__ == "__main__":
    unittest.main()
