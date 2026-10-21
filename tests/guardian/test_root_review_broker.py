import os
from pathlib import Path
import socket
import sqlite3
import struct
import tempfile
import threading
import time
import unittest

from support import BrokerFixture, CHANGES, Observer, OTHER_BASELINE, TOPLEVEL
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from root_review_broker import Denied, GuardianBroker, MAX_MESSAGE, request, serve, store_check


class BrokerDecisionTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture()
        self.addCleanup(self.fx.close)
        self.broker, self.uid = self.fx.broker, self.fx.uid

    def review(self, candidate, attestation=None):
        return self.broker.review(self.uid, {"candidateId": candidate["candidateId"],
                                             "attestation": attestation or self.fx.attest(candidate)})

    def test_submit_binds_observed_baseline_and_host(self):
        candidate = self.fx.submitted()
        self.assertEqual(self.fx.observer.baseline, candidate["baseline"])
        self.assertEqual("prohibited", candidate["activation"])

    def test_full_flow_permits_exact_toplevel(self):
        candidate = self.fx.submitted()
        self.broker.register_approval(candidate["candidateId"], self.uid)
        result = self.review(candidate)
        self.assertTrue(result["eligible"])
        self.assertEqual(TOPLEVEL, result["toplevel"])
        self.assertFalse(result["executed"])
        self.assertTrue(self.broker.verify_ledger())

    def test_review_without_root_approval_is_denied(self):
        candidate = self.fx.submitted()
        with self.assertRaises(Denied):
            self.review(candidate)

    def test_approval_is_single_use_even_after_denial(self):
        candidate = self.fx.submitted()
        self.broker.register_approval(candidate["candidateId"], self.uid)
        forged = self.fx.attest(candidate, key=Ed25519PrivateKey.generate())
        self.assertFalse(self.review(candidate, forged)["eligible"])
        with self.assertRaises(Denied):
            self.review(candidate)  # A valid attestation now has no approval left.

    def test_expired_approval_is_denied(self):
        clock = [time.time()]
        self.broker.clock = lambda: clock[0]
        candidate = self.fx.submitted()
        self.broker.register_approval(candidate["candidateId"], self.uid)
        clock[0] += 121
        with self.assertRaises(Denied):
            self.review(candidate)

    def test_stale_baseline_is_denied(self):
        candidate = self.fx.submitted()
        self.broker.register_approval(candidate["candidateId"], self.uid)
        self.fx.observer.baseline = OTHER_BASELINE  # Someone switched generations after submission.
        self.assertFalse(self.review(candidate)["eligible"])

    def test_failed_check_or_missing_store_output_is_denied(self):
        candidate = self.fx.submitted()
        self.broker.register_approval(candidate["candidateId"], self.uid)
        failed = self.fx.attest(candidate, checks={"evaluate": True, "build": True, "vm-test": False})
        self.assertFalse(self.review(candidate, failed)["eligible"])
        self.broker.register_approval(candidate["candidateId"], self.uid)
        self.fx.store_ok = False
        self.assertFalse(self.review(candidate)["eligible"])

    def test_attestation_for_another_candidate_is_denied(self):
        mine = self.fx.submitted()
        other = self.fx.submitted([{"option": "services.flatpak.enable", "enabled": True}])
        self.broker.register_approval(mine["candidateId"], self.uid)
        self.assertFalse(self.review(mine, self.fx.attest(other))["eligible"])

    def test_unverified_system_refuses_submission(self):
        self.fx.observer.verified = False
        with self.assertRaises(Denied):
            self.fx.submitted()

    def test_unlisted_uid_is_denied_everywhere(self):
        candidate = self.fx.submitted()
        stranger = self.uid + 1
        for call in (lambda: self.broker.submit(stranger, {"description": "x", "changes": CHANGES}),
                     lambda: self.broker.review(stranger, {"candidateId": candidate["candidateId"], "attestation": {}}),
                     lambda: self.broker.status(stranger, {"candidateId": candidate["candidateId"]}),
                     lambda: self.broker.register_approval(candidate["candidateId"], stranger)):
            with self.assertRaises(Denied):
                call()

    def test_invalid_changes_are_denied(self):
        for changes in ([{"option": "imports", "enabled": True}], [], "services.printing.enable"):
            with self.assertRaises(Denied):
                self.broker.submit(self.uid, {"description": "x", "changes": changes})

    def test_staging_claim_is_once_and_only_for_permitted(self):
        candidate = self.fx.submitted()
        self.broker.register_approval(candidate["candidateId"], self.uid)
        decision = self.review(candidate)["decision"]
        self.assertEqual(TOPLEVEL, self.broker.claim_for_staging(decision))
        with self.assertRaises(Denied):
            self.broker.claim_for_staging(decision)
        self.broker.register_approval(candidate["candidateId"], self.uid)
        denied = self.review(candidate, self.fx.attest(candidate, key=Ed25519PrivateKey.generate()))["decision"]
        with self.assertRaises(Denied):
            self.broker.claim_for_staging(denied)
        self.assertTrue(self.broker.status(self.uid, {"candidateId": candidate["candidateId"]})["decision"] is not None)

    def test_ledger_tampering_is_detected(self):
        candidate = self.fx.submitted()
        for _ in range(2):
            self.broker.register_approval(candidate["candidateId"], self.uid)
            self.review(candidate)
        db = sqlite3.connect(self.broker.db_path)
        db.execute("UPDATE decisions SET permitted=0 WHERE seq=1")
        db.commit()
        db.close()
        self.assertFalse(self.broker.verify_ledger())

    def test_state_and_key_permissions_are_enforced(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory) / "state"
            state.mkdir(mode=0o755)
            with self.assertRaises(PermissionError):
                GuardianBroker(state, self.fx.keys, {self.uid}, "path:/x#nixosConfigurations.a", owner=self.uid)
        os.chmod(self.fx.keys, 0o666)
        with self.assertRaises(PermissionError):
            GuardianBroker(self.fx.state, self.fx.keys, {self.uid}, "path:/x#nixosConfigurations.a", owner=self.uid)

    def test_store_check_requires_switch_script_and_matching_deriver(self):
        with tempfile.TemporaryDirectory() as directory:
            fake = {"toplevel": directory, "drvPath": "/nix/store/x.drv"}
            self.assertFalse(store_check(fake, deriver=None))
            (Path(directory) / "bin").mkdir()
            (Path(directory) / "bin" / "switch-to-configuration").write_text("")
            self.assertTrue(store_check(fake, deriver=lambda _: "/nix/store/x.drv"))
            self.assertTrue(store_check(fake, deriver=lambda _: "unknown-deriver"))
            self.assertFalse(store_check(fake, deriver=lambda _: "/nix/store/other.drv"))


class BrokerProtocolTests(unittest.TestCase):
    def setUp(self):
        self.fx = BrokerFixture()
        self.addCleanup(self.fx.close)
        self.socket = self.fx.dir / "broker.sock"
        self.start(self.fx.broker)

    def start(self, broker, **options):
        self.stop = threading.Event()
        ready = threading.Event()
        self.thread = threading.Thread(target=serve, args=(broker, self.socket),
                                       kwargs={"ready": ready, "stop": self.stop, **options}, daemon=True)
        self.thread.start()
        self.assertTrue(ready.wait(5))
        self.addCleanup(self.halt)

    def halt(self):
        self.stop.set()
        self.thread.join(5)

    def raw(self, data, read=True):
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(5)
            client.connect(str(self.socket))
            client.sendall(data)
            if not read:
                return None
            try:
                return client.recv(65536)
            except OSError:
                return b""

    def test_submit_and_status_round_trip(self):
        submitted = request(self.socket, "submit", {"description": "Enable printing", "changes": CHANGES})
        self.assertTrue(submitted["ok"])
        status = request(self.socket, "status", {"candidateId": submitted["candidate"]["candidateId"]})
        self.assertTrue(status["ok"])
        self.assertFalse(status["approvalPending"])

    def test_socket_is_private_by_default(self):
        self.assertEqual(0o600, self.socket.stat().st_mode & 0o777)

    def test_denials_are_reported_without_detail_leakage(self):
        response = request(self.socket, "stage", {})
        self.assertEqual({"ok": False, "error": "Unsupported operation"}, response)
        response = request(self.socket, "review", {"candidateId": "x" * 24, "attestation": {}})
        self.assertFalse(response["ok"])

    def test_oversized_and_malformed_frames_are_refused(self):
        self.assertIn(b"Message size out of bounds", self.raw(struct.pack(">I", MAX_MESSAGE + 1)))
        self.assertIn(b"Message size out of bounds", self.raw(struct.pack(">I", 0)))
        body = b"{not json"
        reply = self.raw(struct.pack(">I", len(body)) + body)
        self.assertIn(b"Malformed request", reply)

    def test_split_frames_are_reassembled(self):
        import json
        body = json.dumps({"operation": "status", "payload": {"candidateId": "z" * 24}}).encode()
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(5)
            client.connect(str(self.socket))
            frame = struct.pack(">I", len(body)) + body
            for byte in frame:
                client.sendall(bytes([byte]))
            self.assertIn(b"No such candidate", client.recv(65536))

    def test_idle_client_does_not_block_others(self):
        self.halt()
        self.start(self.fx.broker, timeout=0.5)
        idle = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        idle.connect(str(self.socket))
        self.addCleanup(idle.close)
        started = time.monotonic()
        self.assertTrue(request(self.socket, "submit", {"description": "x", "changes": CHANGES})["ok"])
        self.assertLess(time.monotonic() - started, 2)

    def test_concurrent_clients_are_served(self):
        results = []
        def call():
            results.append(request(self.socket, "submit", {"description": "x", "changes": CHANGES})["ok"])
        threads = [threading.Thread(target=call) for _ in range(6)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(10)
        self.assertEqual([True] * 6, results)

    def test_unlisted_peer_uid_gets_no_response(self):
        self.halt()
        broker = BrokerFixture(allowed={os.geteuid() + 12345})
        self.addCleanup(broker.close)
        self.start(broker.broker)
        with self.assertRaises((ConnectionError, OSError, struct.error)):
            request(self.socket, "submit", {"description": "x", "changes": CHANGES}, timeout=2)

    def test_stale_socket_is_replaced_but_regular_file_is_not(self):
        self.halt()
        self.assertFalse(self.socket.exists())
        stale = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        stale.bind(str(self.socket))
        stale.close()
        self.start(self.fx.broker)
        self.assertTrue(request(self.socket, "status", {"candidateId": "a" * 24}) is not None)
        self.halt()
        self.socket.write_text("not a socket")
        with self.assertRaises(PermissionError):
            serve(self.fx.broker, self.socket, stop=threading.Event())


if __name__ == "__main__":
    unittest.main()
