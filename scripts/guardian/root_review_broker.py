#!/usr/bin/env python3
"""Root-owned read-only Guardian reviewer. No activation, staging or shell commands."""
import hashlib
import hmac
import json
import os
from pathlib import Path
import socket
import sqlite3
import stat
import struct

from baseline_inspect import inspect
from broker_policy import canonical, verify_attestation
from recovery_gate import eligible

MAX_MESSAGE = 65536

def protected_file(path, owner=0):
    info = os.stat(path, follow_symlinks=False)
    if not stat.S_ISREG(info.st_mode) or info.st_uid != owner or info.st_mode & 0o077:
        raise PermissionError("Untrusted Guardian key or state permissions")

def protected_directory(path, owner=0):
    info = os.stat(path, follow_symlinks=False)
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != owner or info.st_mode & 0o077:
        raise PermissionError("Untrusted Guardian directory permissions")

class GuardianReviewBroker:
    def __init__(self, state_dir, key_path, allowed_uids, *, owner=0, observer=inspect):
        self.state_dir = Path(state_dir)
        self.key_path = Path(key_path)
        protected_directory(self.state_dir, owner)
        protected_file(self.key_path, owner)
        self.key = self.key_path.read_bytes()
        if len(self.key) != 32:
            raise ValueError("Guardian signing secret must be exactly 32 bytes")
        self.allowed_uids = frozenset(allowed_uids)
        self.observer = observer
        self.db_path = self.state_dir / "approvals.sqlite3"
        if not self.db_path.exists():
            fd = os.open(self.db_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
            os.close(fd)
        protected_file(self.db_path, owner)
        with self.connect() as db:
            db.execute("CREATE TABLE IF NOT EXISTS approvals (nonce_hash TEXT PRIMARY KEY, candidate TEXT NOT NULL, uid INTEGER NOT NULL, expires INTEGER NOT NULL, consumed INTEGER NOT NULL DEFAULT 0)")
            db.execute("CREATE TABLE IF NOT EXISTS decisions (id INTEGER PRIMARY KEY, candidate TEXT NOT NULL, uid INTEGER NOT NULL, permitted INTEGER NOT NULL, at INTEGER NOT NULL)")

    def connect(self):
        protected_file(self.db_path, os.geteuid())
        return sqlite3.connect(f"file:{self.db_path}?mode=rw", uri=True, timeout=5)

    def register(self, nonce, candidate_id, uid, expires, *, now):
        if uid not in self.allowed_uids or not isinstance(nonce, str) or len(nonce) < 32 or not candidate_id or expires <= now or expires > now + 300:
            return False
        digest = hashlib.sha256(nonce.encode()).hexdigest()
        with self.connect() as db:
            try:
                db.execute("INSERT INTO approvals(nonce_hash,candidate,uid,expires) VALUES(?,?,?,?)", (digest,candidate_id,uid,expires))
            except sqlite3.IntegrityError:
                return False
        return True

    def review(self, uid, request, *, now):
        if uid not in self.allowed_uids or not isinstance(request, dict) or set(request) != {"candidate","changes","evidence","attestation","nonce"}:
            return False
        candidate = request["candidate"]
        if not isinstance(candidate, dict) or not isinstance(request["nonce"], str):
            return False
        observation = self.observer()
        if observation.get("verified") is not True:
            return False
        baseline = observation.get("generationStorePath")
        candidate_id = candidate.get("candidateId")
        if not isinstance(candidate_id, str):
            return False
        digest = hashlib.sha256(request["nonce"].encode()).hexdigest()
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            cursor = db.execute("UPDATE approvals SET consumed=1 WHERE nonce_hash=? AND candidate=? AND uid=? AND expires>? AND consumed=0",
                                (digest,candidate_id,uid,now))
            if cursor.rowcount != 1:
                db.rollback()
                return False
            try:
                allowed = (verify_attestation(candidate,request["evidence"],request["attestation"],self.key)
                           and eligible(candidate,request["changes"],request["evidence"],baseline))
            except (TypeError, ValueError, KeyError):
                allowed = False
            db.execute("INSERT INTO decisions(candidate,uid,permitted,at) VALUES(?,?,?,?)",
                       (candidate_id,uid,int(allowed),now))
            db.commit()
        return bool(allowed)

def peer_uid(connection):
    data = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
    return struct.unpack("3i", data)[1]

def serve(broker, socket_path):
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.bind(socket_path)
    os.chmod(socket_path, 0o600)
    sock.listen(16)
    try:
        while True:
            connection, _ = sock.accept()
            with connection:
                try:
                    uid = peer_uid(connection)
                    if uid not in broker.allowed_uids:
                        continue
                    data = connection.recv(MAX_MESSAGE + 1)
                    if len(data) > MAX_MESSAGE:
                        continue
                    request = json.loads(data)
                    # Intentionally only a non-activating review verb is exposed.
                    if not isinstance(request, dict) or request.get("operation") != "review":
                        continue
                    payload = request.get("payload")
                    permitted = broker.review(uid,payload,now=int(__import__("time").time()))
                    connection.sendall(canonical({"eligibleForReview":permitted,"executed":False}) + b"\n")
                except (OSError, ValueError, TypeError, KeyError):
                    continue
    finally:
        sock.close()
        os.unlink(socket_path)
