#!/usr/bin/env python3
"""Root-owned Guardian review broker (ADR-002). Decides; never stages or activates.

Protocol: each message is a 4-byte big-endian length followed by UTF-8 JSON
({"operation": ..., "payload": {...}}), at most MAX_MESSAGE bytes, one request
per connection. Callers are identified by kernel SO_PEERCRED and must be in
the configured UID allowlist. Operations:

  submit  {description, changes}         -> candidate for the observed system
  review  {candidateId, attestation}     -> decision (consumes a root approval)
  status  {candidateId}                  -> candidate, approval and decision state
"""
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import stat
import struct
import subprocess
import threading
import time

from attestation import load_trusted_keys, verify
from baseline_inspect import inspect
from candidate import prepare
from guardian_schema import ID_LENGTH, canonical
from recovery_gate import eligible

MAX_MESSAGE = 65536
APPROVAL_SECONDS = 120


class Denied(Exception):
    """A request the broker refuses; the message is safe to return to the caller."""


def protected_file(path, owner=0, *, allow_world_read=False):
    info = os.stat(path, follow_symlinks=False)
    forbidden = 0o022 if allow_world_read else 0o077
    if not stat.S_ISREG(info.st_mode) or info.st_uid != owner or info.st_mode & forbidden:
        raise PermissionError("Untrusted Guardian file permissions")


def protected_directory(path, owner=0):
    info = os.stat(path, follow_symlinks=False)
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != owner or info.st_mode & 0o077:
        raise PermissionError("Untrusted Guardian directory permissions")


def nix_deriver(toplevel):
    """Ask the local Nix store which derivation produced a path (read-only query)."""
    result = subprocess.run(["nix-store", "--query", "--deriver", toplevel],
                            capture_output=True, text=True, timeout=20, check=False)
    return result.stdout.strip() if result.returncode == 0 else None


def store_check(statement, deriver=nix_deriver):
    toplevel = statement["toplevel"]
    if not os.path.isfile(os.path.join(toplevel, "bin", "switch-to-configuration")):
        return False
    produced_by = deriver(toplevel) if deriver else None
    # "unknown-deriver" is reported for substituted paths; only a contradiction denies.
    return produced_by in (None, "unknown-deriver", statement["drvPath"])


class GuardianBroker:
    def __init__(self, state_dir, trusted_keys_path, allowed_uids, host_config, *, owner=0,
                 observer=inspect, store_checker=store_check, clock=time.time):
        self.state_dir = Path(state_dir)
        protected_directory(self.state_dir, owner)
        self.trusted_keys = load_trusted_keys(trusted_keys_path, owner=owner)
        if not self.trusted_keys:
            raise ValueError("At least one trusted builder key is required")
        self.allowed_uids = frozenset(allowed_uids)
        self.host_config = host_config
        self.owner = owner
        self.observer = observer
        self.store_checker = store_checker
        self.clock = clock
        self.db_path = self.state_dir / "guardian.sqlite3"
        if not self.db_path.exists():
            os.close(os.open(self.db_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600))
        protected_file(self.db_path, owner)
        with closing(self.connect()) as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS candidates (candidate TEXT PRIMARY KEY, uid INTEGER NOT NULL,
                    record TEXT NOT NULL, created INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS approvals (id TEXT PRIMARY KEY, candidate TEXT NOT NULL,
                    uid INTEGER NOT NULL, expires INTEGER NOT NULL, consumed INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS decisions (seq INTEGER PRIMARY KEY AUTOINCREMENT, candidate TEXT NOT NULL,
                    uid INTEGER NOT NULL, permitted INTEGER NOT NULL, toplevel TEXT, at INTEGER NOT NULL,
                    staged INTEGER NOT NULL DEFAULT 0, previous TEXT NOT NULL, hash TEXT NOT NULL UNIQUE);
            """)

    def connect(self):
        protected_file(self.db_path, self.owner)
        db = sqlite3.connect(f"file:{self.db_path}?mode=rw", uri=True, timeout=5, isolation_level=None)
        db.execute("PRAGMA foreign_keys=ON")
        return db

    def now(self):
        return int(self.clock())

    def _authorise(self, uid):
        if uid not in self.allowed_uids:
            raise Denied("Caller is not authorised")

    # --- operations -------------------------------------------------------

    def submit(self, uid, payload):
        self._authorise(uid)
        if not isinstance(payload, dict) or set(payload) != {"description", "changes"}:
            raise Denied("submit expects description and changes")
        description = payload["description"]
        if not isinstance(description, str) or not 1 <= len(description.strip()) <= 500:
            raise Denied("Description must be 1-500 characters")
        observation = self.observer()
        if observation.get("verified") is not True:
            raise Denied("Running system could not be verified")
        try:
            candidate = prepare(payload["changes"], observation["generationStorePath"], self.host_config)
        except ValueError as error:
            raise Denied(str(error)) from None
        record = {"candidate": candidate, "changes": payload["changes"], "description": description.strip(),
                  "submittedBy": uid, "submittedAt": self.now()}
        with closing(self.connect()) as db:
            db.execute("INSERT OR IGNORE INTO candidates(candidate,uid,record,created) VALUES(?,?,?,?)",
                       (candidate["candidateId"], uid, json.dumps(record, sort_keys=True), self.now()))
        return {"candidate": candidate, "approval": "required", "executed": False}

    def register_approval(self, candidate_id, uid):
        """Root-only (never reachable over the socket): approve one candidate for one UID, once."""
        if uid not in self.allowed_uids:
            raise Denied("UID is not allowlisted")
        with closing(self.connect()) as db:
            row = db.execute("SELECT uid FROM candidates WHERE candidate=?", (candidate_id,)).fetchone()
            if row is None or row[0] != uid:
                raise Denied("No such candidate submitted by this UID")
            approval = hashlib.sha256(os.urandom(32)).hexdigest()[:32]
            db.execute("INSERT INTO approvals(id,candidate,uid,expires) VALUES(?,?,?,?)",
                       (approval, candidate_id, uid, self.now() + APPROVAL_SECONDS))
        return approval

    def review(self, uid, payload):
        self._authorise(uid)
        if not isinstance(payload, dict) or set(payload) != {"candidateId", "attestation"}:
            raise Denied("review expects candidateId and attestation")
        candidate_id = payload["candidateId"]
        if not isinstance(candidate_id, str) or len(candidate_id) != ID_LENGTH:
            raise Denied("Invalid candidate ID")
        db = self.connect()
        try:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT record FROM candidates WHERE candidate=? AND uid=?", (candidate_id, uid)).fetchone()
            if row is None:
                db.execute("ROLLBACK")
                raise Denied("No such candidate for this caller")
            # The approval is single use whatever the outcome, so a denied
            # review cannot be retried against the same approval.
            consumed = db.execute("UPDATE approvals SET consumed=1 WHERE candidate=? AND uid=? AND consumed=0 AND expires>?",
                                  (candidate_id, uid, self.now())).rowcount
            if consumed < 1:
                db.execute("ROLLBACK")
                raise Denied("No unused, unexpired root approval for this candidate")
            record = json.loads(row[0])
            permitted, toplevel = self._evaluate(record, payload["attestation"])
            decision = self._record_decision(db, candidate_id, uid, permitted, toplevel)
            db.execute("COMMIT")
        except BaseException:
            if db.in_transaction:
                db.execute("ROLLBACK")
            raise
        finally:
            db.close()
        return {"candidateId": candidate_id, "eligible": permitted, "decision": decision,
                "toplevel": toplevel if permitted else None, "executed": False}

    def _evaluate(self, record, attestation):
        statement = verify(attestation, self.trusted_keys, now=self.now())
        if statement is None:
            return False, None
        observation = self.observer()
        if observation.get("verified") is not True:
            return False, None
        try:
            ok = eligible(record["candidate"], record["changes"], statement, observation["generationStorePath"])
        except (ValueError, KeyError, TypeError):
            return False, None
        if not ok or not self.store_checker(statement):
            return False, None
        return True, statement["toplevel"]

    def _record_decision(self, db, candidate_id, uid, permitted, toplevel):
        previous = db.execute("SELECT hash FROM decisions ORDER BY seq DESC LIMIT 1").fetchone()
        previous = previous[0] if previous else "GENESIS"
        at = self.now()
        digest = hashlib.sha256(canonical({"candidate": candidate_id, "uid": uid, "permitted": permitted,
                                           "toplevel": toplevel, "at": at, "previous": previous})).hexdigest()
        db.execute("INSERT INTO decisions(candidate,uid,permitted,toplevel,at,previous,hash) VALUES(?,?,?,?,?,?,?)",
                   (candidate_id, uid, int(permitted), toplevel, at, previous, digest))
        return digest

    def status(self, uid, payload):
        self._authorise(uid)
        if not isinstance(payload, dict) or set(payload) != {"candidateId"}:
            raise Denied("status expects candidateId")
        with closing(self.connect()) as db:
            row = db.execute("SELECT record FROM candidates WHERE candidate=? AND uid=?", (payload["candidateId"], uid)).fetchone()
            if row is None:
                raise Denied("No such candidate for this caller")
            pending = db.execute("SELECT COUNT(*) FROM approvals WHERE candidate=? AND uid=? AND consumed=0 AND expires>?",
                                 (payload["candidateId"], uid, self.now())).fetchone()[0]
            decision = db.execute("SELECT permitted, hash, staged FROM decisions WHERE candidate=? AND uid=? ORDER BY seq DESC LIMIT 1",
                                  (payload["candidateId"], uid)).fetchone()
        return {"candidate": json.loads(row[0])["candidate"], "approvalPending": pending > 0,
                "decision": None if decision is None else
                {"eligible": bool(decision[0]), "hash": decision[1], "staged": bool(decision[2])}}

    # --- staging executor interface (root-only, not exposed on the socket) ---

    def claim_for_staging(self, decision_hash):
        """Atomically mark a permitted, unstaged decision as staged; return its toplevel."""
        with closing(self.connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT toplevel FROM decisions WHERE hash=? AND permitted=1 AND staged=0",
                             (decision_hash,)).fetchone()
            if row is None:
                db.execute("ROLLBACK")
                raise Denied("No permitted, unstaged decision with that hash")
            db.execute("UPDATE decisions SET staged=1 WHERE hash=?", (decision_hash,))
            db.execute("COMMIT")
        return row[0]

    def verify_ledger(self):
        previous = "GENESIS"
        with closing(self.connect()) as db:
            for candidate, uid, permitted, toplevel, at, prev, digest in db.execute(
                    "SELECT candidate,uid,permitted,toplevel,at,previous,hash FROM decisions ORDER BY seq"):
                expected = hashlib.sha256(canonical({"candidate": candidate, "uid": uid, "permitted": bool(permitted),
                                                     "toplevel": toplevel, "at": at, "previous": prev})).hexdigest()
                if prev != previous or digest != expected:
                    return False
                previous = digest
        return True

    OPERATIONS = ("submit", "review", "status")

    def handle(self, uid, request):
        if not isinstance(request, dict) or set(request) != {"operation", "payload"}:
            raise Denied("Malformed request")
        if request["operation"] not in self.OPERATIONS:
            raise Denied("Unsupported operation")
        return getattr(self, request["operation"])(uid, request["payload"])


# --- wire protocol --------------------------------------------------------

def read_exact(connection, count):
    data = bytearray()
    while len(data) < count:
        chunk = connection.recv(count - len(data))
        if not chunk:
            raise ConnectionError("Connection closed mid-message")
        data.extend(chunk)
    return bytes(data)


def read_message(connection):
    (length,) = struct.unpack(">I", read_exact(connection, 4))
    if not 0 < length <= MAX_MESSAGE:
        raise Denied("Message size out of bounds")
    return json.loads(read_exact(connection, length).decode("utf-8"))


def write_message(connection, value):
    data = canonical(value)
    connection.sendall(struct.pack(">I", len(data)) + data)


def peer_uid(connection):
    data = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
    return struct.unpack("3i", data)[1]


def handle_connection(broker, connection, timeout):
    with connection:
        connection.settimeout(timeout)
        try:
            uid = peer_uid(connection)
            if uid not in broker.allowed_uids:
                return  # Unknown callers learn nothing, not even the protocol.
            try:
                response = {"ok": True, **broker.handle(uid, read_message(connection))}
            except Denied as denied:
                response = {"ok": False, "error": str(denied)}
            except (ValueError, UnicodeDecodeError, json.JSONDecodeError):
                response = {"ok": False, "error": "Malformed request"}
            write_message(connection, response)
        except (OSError, ConnectionError, struct.error):
            pass  # Timeouts, resets and truncated frames end the connection silently.


def serve(broker, socket_path, *, group=None, max_clients=8, timeout=5.0, ready=None, stop=None):
    path = Path(socket_path)
    if path.exists() or path.is_symlink():
        if not stat.S_ISSOCK(os.lstat(path).st_mode):
            raise PermissionError("Refusing to replace a non-socket at the broker path")
        path.unlink()  # Stale socket from an unclean stop, in a root-owned runtime directory.
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.bind(str(path))
    os.chmod(path, 0o660 if group is not None else 0o600)
    if group is not None:
        os.chown(path, -1, group)
    sock.listen(16)
    sock.settimeout(0.5)
    slots = threading.BoundedSemaphore(max_clients)
    if ready is not None:
        ready.set()
    try:
        while stop is None or not stop.is_set():
            try:
                connection, _ = sock.accept()
            except socket.timeout:
                continue
            if not slots.acquire(blocking=False):
                connection.close()  # Over capacity: shed load rather than queue unboundedly.
                continue

            def run(conn=connection):
                try:
                    handle_connection(broker, conn, timeout)
                finally:
                    slots.release()
            threading.Thread(target=run, daemon=True).start()
    finally:
        sock.close()
        path.unlink(missing_ok=True)


def request(socket_path, operation, payload, *, timeout=10.0):
    """Client helper used by tests and tools."""
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
        client.settimeout(timeout)
        client.connect(str(socket_path))
        write_message(client, {"operation": operation, "payload": payload})
        return read_message(client)
