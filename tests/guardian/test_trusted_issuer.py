import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "guardian"))
from root_approval_issuer import require_root_console, provision_key, issue
from root_review_broker import GuardianReviewBroker

class TrustedIssuerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        os.chmod(self.root, 0o700)
        self.uid = os.geteuid()
        self.key = provision_key(self.root, owner=self.uid)
        self.broker = GuardianReviewBroker(self.root, self.key, {self.uid}, owner=self.uid)
        self.candidate = "a" * 24

    def test_key_is_private_and_cannot_be_overwritten(self):
        self.assertEqual(32, self.key.stat().st_size)
        self.assertEqual(0o600, self.key.stat().st_mode & 0o777)
        with self.assertRaises(FileExistsError):
            provision_key(self.root, owner=self.uid)

    def test_root_console_required(self):
        with self.assertRaises(PermissionError):
            require_root_console(uid=1000, stdin_tty=True, stdout_tty=True)
        with self.assertRaises(PermissionError):
            require_root_console(uid=0, stdin_tty=False, stdout_tty=True)
        with self.assertRaises(PermissionError):
            require_root_console(uid=0, stdin_tty=True, stdout_tty=False)
        require_root_console(uid=0, stdin_tty=True, stdout_tty=True)

    def test_explicit_confirmation_and_single_use_nonce(self):
        with patch("root_approval_issuer.require_root_console"):
            with self.assertRaises(PermissionError):
                issue(self.broker, self.candidate, self.uid, "APPROVE", now=100)
            nonce = issue(self.broker, self.candidate, self.uid,
                "ISSUE REVIEW " + self.candidate, now=100)
        self.assertGreaterEqual(len(nonce), 32)
        self.assertTrue(self.broker.connect().execute(
            "SELECT count(*) FROM approvals WHERE consumed=0").fetchone()[0] == 1)

    def test_wrong_client_uid_rejected(self):
        with patch("root_approval_issuer.require_root_console"):
            with self.assertRaises(PermissionError):
                issue(self.broker, self.candidate, self.uid + 1,
                    "ISSUE REVIEW " + self.candidate, now=100)

    def test_invalid_candidate_identity_rejected(self):
        with patch("root_approval_issuer.require_root_console"):
            with self.assertRaises(ValueError):
                issue(self.broker, "../bad", self.uid, "ISSUE REVIEW ../bad", now=100)

    def test_insecure_directory_rejected(self):
        os.chmod(self.root, 0o755)
        with self.assertRaises(PermissionError):
            provision_key(self.root, owner=self.uid)

    def test_systemd_unit_disallows_network_and_exec_privilege(self):
        unit = (Path(__file__).resolve().parents[2] / "nix" / "guardian" /
                "nexus-guardian-review.service").read_text()
        for directive in ("NoNewPrivileges=yes", "ProtectSystem=strict",
                          "RestrictAddressFamilies=AF_UNIX", "IPAddressDeny=any",
                          "CapabilityBoundingSet=", "RuntimeDirectoryMode=0700",
                          "StateDirectoryMode=0700", "User=root"):
            self.assertIn(directive, unit)
        self.assertNotIn("nixos-rebuild", unit)
