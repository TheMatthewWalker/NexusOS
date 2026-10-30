"""Desktop (polkit) approval is bound to the candidate and build the person was shown.

The dialog shows the broker's own record (describe); polkit's prompt names the
helper's command line (candidate ID and build hash); after the password the
helper re-reads the candidate from the broker and registers an approval for
exactly that candidate and build."""
import io
import json
import time
import unittest
from contextlib import redirect_stdout

from support import CHANGES, TOPLEVEL, BrokerFixture
from attestation import build_hash
from desktop_approve import approve, main, requesting_uid
from root_review_broker import Denied

BEFORE = {"options": {"services.printing.enable": False}, "packages": {}}


class DesktopApprovalTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture()
        self.addCleanup(self.fx.close)
        self.candidate = self.fx.submitted()
        self.id = self.candidate["candidateId"]
        self.attestation = self.fx.attest(self.candidate, before=BEFORE)
        self.record = self.fx.broker.describe(self.fx.uid, {"candidateId": self.id, "attestation": self.attestation})

    def pending(self):
        return self.fx.broker.status(self.fx.uid, {"candidateId": self.id})["approvalPending"]

    def review(self, attestation):
        return self.fx.broker.review(self.fx.uid, {"candidateId": self.id, "attestation": attestation})

    def test_only_polkit_started_non_root_requests(self):
        self.assertEqual(1000, requesting_uid({"PKEXEC_UID": "1000"}, 0))
        for environ, euid in (({"PKEXEC_UID": "1000"}, 1000), ({}, 0), ({"PKEXEC_UID": "0"}, 0),
                              ({"PKEXEC_UID": "1000; x"}, 0), ({"PKEXEC_UID": ""}, 0)):
            with self.subTest(environ=environ, euid=euid), self.assertRaises(PermissionError):
                requesting_uid(environ, euid)

    def test_the_helper_needs_both_the_candidate_and_the_build(self):
        for argv in ([self.id], [self.id, self.record["buildHash"], "extra"], []):
            output = io.StringIO()
            with self.subTest(argv=argv), redirect_stdout(output):
                self.assertEqual(1, main(argv, {"PKEXEC_UID": str(self.fx.uid)}, 0))
            self.assertFalse(json.loads(output.getvalue())["approved"])
        self.assertFalse(self.pending())

    def test_describe_is_the_brokers_record_with_the_signed_values(self):
        self.assertEqual(self.id, self.record["candidateId"])
        self.assertEqual(build_hash(self.attestation["statement"]), self.record["buildHash"])
        self.assertEqual([{"option": "services.printing.enable", "summary": "CUPS printing service",
                           "before": False, "after": True}], self.record["settings"])
        self.assertEqual(([], TOPLEVEL, False), (self.record["packages"], self.record["toplevel"], self.record["staged"]))
        self.assertNotIn("description", self.record)  # The submitter's own words are not the broker's record.
        # A statement without today's values still describes the change; the values are unknown.
        old = self.fx.broker.describe(self.fx.uid, {"candidateId": self.id, "attestation": self.fx.attest(self.candidate)})
        self.assertIsNone(old["settings"][0]["before"])

    def test_describe_refuses_what_it_cannot_verify(self):
        other = self.fx.submitted([{"option": "services.flatpak.enable", "enabled": True}])
        failing = self.fx.attest(self.candidate, checks={"evaluate": True, "build": False, "vm-test": True})
        forged = json.loads(json.dumps(self.attestation))
        forged["statement"]["before"]["options"]["services.printing.enable"] = True
        for uid, payload in ((self.fx.uid, {"candidateId": self.id, "attestation": failing}),
                             (self.fx.uid, {"candidateId": self.id, "attestation": forged}),
                             (self.fx.uid, {"candidateId": self.id, "attestation": self.fx.attest(other)}),
                             (self.fx.uid, {"candidateId": "f" * 24, "attestation": self.attestation}),
                             (self.fx.uid, {"candidateId": self.id}),
                             (self.fx.uid + 1, {"candidateId": self.id, "attestation": self.attestation})):
            with self.subTest(payload=payload), self.assertRaises(Denied):
                self.fx.broker.describe(uid, payload)

    def test_refuses_an_unknown_or_malformed_candidate(self):
        for candidate_id, build in (("f" * 24, self.record["buildHash"]), ("not-a-candidate", self.record["buildHash"]),
                                    (self.id, "not-a-hash"), (self.id, self.record["buildHash"].upper())):
            with self.subTest(candidate_id=candidate_id, build=build), self.assertRaises(Denied):
                approve(self.fx.broker, candidate_id, build, self.fx.uid)
        self.assertFalse(self.pending())

    def test_refuses_another_persons_candidate(self):
        self.fx.broker.allowed_uids = frozenset({self.fx.uid, self.fx.uid + 1})
        with self.assertRaises(Denied):
            approve(self.fx.broker, self.id, self.record["buildHash"], self.fx.uid + 1)
        self.assertFalse(self.pending())

    def test_refuses_a_build_other_than_the_one_guardian_checked(self):
        with self.assertRaises(Denied):
            approve(self.fx.broker, self.id, "0" * 64, self.fx.uid)
        # A different (validly signed) build described after the dialog showed this one.
        self.fx.broker.describe(self.fx.uid, {"candidateId": self.id,
                                              "attestation": self.fx.attest(self.candidate, issuedAt=int(time.time()) - 60)})
        with self.assertRaises(Denied):
            approve(self.fx.broker, self.id, self.record["buildHash"], self.fx.uid)
        self.assertFalse(self.pending())

    def test_refuses_a_candidate_never_described(self):
        unchecked = self.fx.submitted([{"option": "services.flatpak.enable", "enabled": True}])
        build = build_hash(self.fx.attest(unchecked)["statement"])
        with self.assertRaises(Denied):
            approve(self.fx.broker, unchecked["candidateId"], build, self.fx.uid)

    def test_refuses_a_staged_candidate(self):
        self.fx.broker.register_approval(self.id, self.fx.uid)  # the root console's, for any build
        decision = self.review(self.attestation)["decision"]
        self.fx.broker.claim_for_staging(decision)
        with self.assertRaises(Denied):
            approve(self.fx.broker, self.id, self.record["buildHash"], self.fx.uid)
        self.assertFalse(self.pending())

    def test_registers_one_approval_bound_to_the_candidate_and_build(self):
        approve(self.fx.broker, self.id, self.record["buildHash"], self.fx.uid)
        self.assertTrue(self.pending())
        # Another validly signed build of the same candidate cannot use it, and does not use it up.
        other = self.fx.attest(self.candidate, issuedAt=int(time.time()) - 60)
        with self.assertRaises(Denied):
            self.review(other)
        self.assertTrue(self.pending())
        # The build the person was shown can, once.
        self.assertTrue(self.review(self.attestation)["eligible"])
        self.assertFalse(self.pending())
        with self.assertRaises(Denied):
            self.review(self.attestation)


if __name__ == "__main__":
    unittest.main()
