#!/usr/bin/env python3
"""Ed25519 build attestations bound to built NixOS outputs (ADR-002).

The builder holds the private key and signs; the broker holds only public keys
and verifies. A statement names the exact system toplevel and derivation that
passed evaluation, build and VM testing for one candidate, and may record
today's value of each changed setting and package as the builder evaluated it
("before"), so what the person is shown at approval is signed as well.
"""
import argparse
import base64
import hashlib
import json
import os
import stat
import sys
import time
from pathlib import Path

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
from cryptography.hazmat.primitives.serialization import Encoding, NoEncryption, PrivateFormat, PublicFormat

from guardian_schema import DRV_PATH, ID_LENGTH, STORE_PATH, canonical, require_baseline

STATEMENT_FIELDS = {"attestationVersion", "candidateId", "baseline", "toplevel", "drvPath", "checks", "builder", "issuedAt"}
OPTIONAL_FIELDS = {"before"}
CHECKS = ("evaluate", "build", "vm-test")
MAX_AGE_SECONDS = 7 * 24 * 3600
MAX_BEFORE = 64


def key_id(public_raw):
    return hashlib.sha256(public_raw).hexdigest()[:16]


def public_raw(private_key):
    return private_key.public_key().public_bytes(Encoding.Raw, PublicFormat.Raw)


def build_hash(statement):
    """The identity of one signed build: what the person approves, and what the review must present."""
    return hashlib.sha256(canonical(statement)).hexdigest()


def validate_before(before):
    """{"options": {name: bool|null}, "packages": {attr: bool|null}}; null where today's value is unknown."""
    if not isinstance(before, dict) or set(before) != {"options", "packages"}:
        raise ValueError("before must have options and packages")
    for section in before.values():
        if not isinstance(section, dict) or len(section) > MAX_BEFORE:
            raise ValueError("Invalid before section")
        for name, value in section.items():
            if not isinstance(name, str) or not 1 <= len(name) <= 200 or not (value is None or type(value) is bool):
                raise ValueError("Invalid before value")
    return before


def validate_statement(statement):
    if not isinstance(statement, dict) or not STATEMENT_FIELDS <= set(statement) <= STATEMENT_FIELDS | OPTIONAL_FIELDS:
        raise ValueError("Unexpected attestation statement fields")
    if statement["attestationVersion"] != 1:
        raise ValueError("Unsupported attestation version")
    if not isinstance(statement["candidateId"], str) or len(statement["candidateId"]) != ID_LENGTH:
        raise ValueError("Invalid candidate ID")
    require_baseline(statement["baseline"])
    if not isinstance(statement["toplevel"], str) or STORE_PATH.fullmatch(statement["toplevel"]) is None:
        raise ValueError("toplevel must be a /nix/store path")
    if not isinstance(statement["drvPath"], str) or DRV_PATH.fullmatch(statement["drvPath"]) is None:
        raise ValueError("drvPath must be a /nix/store .drv path")
    checks = statement["checks"]
    if not isinstance(checks, dict) or set(checks) != set(CHECKS) or not all(type(v) is bool for v in checks.values()):
        raise ValueError("checks must report evaluate, build and vm-test as booleans")
    if not isinstance(statement["builder"], str) or not 1 <= len(statement["builder"]) <= 64:
        raise ValueError("Invalid builder name")
    if type(statement["issuedAt"]) is not int:
        raise ValueError("issuedAt must be an integer")
    if "before" in statement:
        validate_before(statement["before"])
    return statement


def sign(statement, private_key):
    validate_statement(statement)
    raw = public_raw(private_key)
    return {"statement": statement, "keyId": key_id(raw),
            "signature": base64.b64encode(private_key.sign(canonical(statement))).decode("ascii")}


def verify(attestation, trusted_keys, *, now=None):
    """Return the verified statement, or None. trusted_keys maps keyId -> raw public bytes."""
    now = int(time.time()) if now is None else now
    try:
        if not isinstance(attestation, dict) or set(attestation) != {"statement", "keyId", "signature"}:
            return None
        statement = validate_statement(attestation["statement"])
        public = trusted_keys.get(attestation["keyId"]) if isinstance(attestation["keyId"], str) else None
        if public is None or not isinstance(attestation["signature"], str):
            return None
        Ed25519PublicKey.from_public_bytes(public).verify(
            base64.b64decode(attestation["signature"], validate=True), canonical(statement))
        if not now - MAX_AGE_SECONDS <= statement["issuedAt"] <= now + 300:
            return None
        return statement
    except (InvalidSignature, ValueError, TypeError, base64.binascii.Error):
        return None


def load_trusted_keys(path, *, owner=0):
    """Public keys file: one base64 raw Ed25519 key per line. Must not be writable by others."""
    info = os.stat(path, follow_symlinks=False)
    if not stat.S_ISREG(info.st_mode) or info.st_uid != owner or info.st_mode & 0o022:
        raise PermissionError("Trusted builder key file has unsafe ownership or permissions")
    keys = {}
    for line in Path(path).read_text(encoding="ascii").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        raw = base64.b64decode(line, validate=True)
        Ed25519PublicKey.from_public_bytes(raw)
        keys[key_id(raw)] = raw
    return keys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    keygen = sub.add_parser("keygen", help="create a builder signing key (private 0600) and print its public key")
    keygen.add_argument("--private-key", type=Path, required=True)
    signer = sub.add_parser("sign")
    signer.add_argument("--private-key", type=Path, required=True)
    signer.add_argument("--candidate", type=Path, required=True)
    signer.add_argument("--toplevel", required=True)
    signer.add_argument("--drv-path", required=True)
    signer.add_argument("--builder", required=True)
    for check in CHECKS:
        signer.add_argument(f"--{check}", choices=("pass", "fail"), required=True)
    args = parser.parse_args()

    if args.command == "keygen":
        key = Ed25519PrivateKey.generate()
        fd = os.open(args.private_key, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "wb") as stream:
            stream.write(key.private_bytes(Encoding.Raw, PrivateFormat.Raw, NoEncryption()))
        print(base64.b64encode(public_raw(key)).decode("ascii"))
        return
    key = Ed25519PrivateKey.from_private_bytes(args.private_key.read_bytes())
    candidate = json.loads(args.candidate.read_text())
    statement = {
        "attestationVersion": 1, "candidateId": candidate["candidateId"], "baseline": candidate["baseline"],
        "toplevel": args.toplevel, "drvPath": args.drv_path, "builder": args.builder,
        "checks": {check: getattr(args, check.replace("-", "_")) == "pass" for check in CHECKS},
        "issuedAt": int(time.time()),
    }
    print(json.dumps(sign(statement, key), indent=2, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError) as exc:
        print(f"Attestation failed: {exc}", file=sys.stderr)
        sys.exit(2)
