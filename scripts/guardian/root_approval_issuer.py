#!/usr/bin/env python3
"""Root-console-only approval issuance and key provisioning for the read-only broker."""
import argparse
import os
from pathlib import Path
import re
import secrets
import sys
import time
from root_review_broker import GuardianReviewBroker, protected_directory

CANDIDATE_ID = re.compile(r"[a-f0-9]{24}\Z")

def require_root_console(*, uid=None, stdin_tty=None, stdout_tty=None):
    uid = os.geteuid() if uid is None else uid
    stdin_tty = sys.stdin.isatty() if stdin_tty is None else stdin_tty
    stdout_tty = sys.stdout.isatty() if stdout_tty is None else stdout_tty
    if uid != 0 or not stdin_tty or not stdout_tty:
        raise PermissionError("Guardian approval issuance requires an interactive root console")

def provision_key(directory, *, owner=0):
    protected_directory(directory, owner)
    path = Path(directory) / "signing.key"
    # Never rotate keys implicitly; no links or overwrite of existing keys.
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(fd, "wb", closefd=True) as file:
            file.write(secrets.token_bytes(32))
            file.flush()
            os.fsync(file.fileno())
    except BaseException:
        path.unlink(missing_ok=True)
        raise
    return path

def issue(broker, candidate_id, client_uid, confirmation, *, now=None):
    require_root_console()
    if not isinstance(candidate_id, str) or CANDIDATE_ID.fullmatch(candidate_id) is None:
        raise ValueError("Expected a validated candidate ID")
    if confirmation != "ISSUE REVIEW " + candidate_id:
        raise PermissionError("Explicit review issuance confirmation does not match the candidate")
    if not isinstance(client_uid, int) or client_uid < 0 or client_uid not in broker.allowed_uids:
        raise PermissionError("Client UID not allowlisted")
    now = int(time.time()) if now is None else now
    nonce = secrets.token_urlsafe(36)
    if not broker.register(nonce, candidate_id, client_uid, now + 120, now=now):
        raise PermissionError("Cannot register approval")
    return nonce

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("provision-key", "issue", "serve"))
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/nexus-guardian"))
    parser.add_argument("--socket", type=Path, default=Path("/run/nexus-guardian/review.sock"))
    parser.add_argument("--client-uid", type=int, default=0)
    parser.add_argument("--candidate-id")
    args = parser.parse_args()

    # Both the issuer and the service are root-only. No user-accessible root socket yet.
    if os.geteuid() != 0:
        parser.error("Root UID required")
    if args.client_uid != 0:
        parser.error("Only root UID is authorised until a separately trusted mediation path exists")
    protected_directory(args.state_dir)
    if args.operation == "provision-key":
        require_root_console()
        path = provision_key(args.state_dir)
        print(f"Protected signing key provisioned at {path}")
        return
    broker = GuardianReviewBroker(args.state_dir, args.state_dir / "signing.key", {0})
    if args.operation == "issue":
        require_root_console()
        if args.candidate_id is None:
            parser.error("--candidate-id is required for issuance")
        confirmation = input("Type ISSUE REVIEW <candidate-id> exactly: ")
        nonce = issue(broker, args.candidate_id, args.client_uid, confirmation)
        print(nonce)
    else:
        from root_review_broker import serve
        # Socket parent is managed by systemd RuntimeDirectory, root-owned.
        protected_directory(args.socket.parent)
        serve(broker, str(args.socket))

if __name__ == "__main__":
    main()
