#!/usr/bin/env python3
"""Independent Guardian policy decision engine. Read-only: never deploys."""
import hashlib
import hmac
import json
from recovery_gate import eligible

def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")

def verify_attestation(candidate, evidence, attestation, key):
    if not isinstance(attestation, dict) or set(attestation) != {"candidateId", "evidenceSha256", "signature"}:
        return False
    if not isinstance(key, bytes) or len(key) < 32:
        return False
    evidence_hash = hashlib.sha256(canonical(evidence)).hexdigest()
    if attestation["candidateId"] != candidate.get("candidateId") or attestation["evidenceSha256"] != evidence_hash:
        return False
    message = canonical({"candidateId": attestation["candidateId"], "evidenceSha256": evidence_hash})
    signature = hmac.new(key, message, hashlib.sha256).hexdigest()
    return isinstance(attestation["signature"], str) and hmac.compare_digest(signature, attestation["signature"])

def decide(candidate, changes, evidence, attestation, key, observed_baseline, approval):
    # No user-controlled approval text or CI JSON alone can authorise a system change.
    if not isinstance(approval, dict) or set(approval) != {"candidateId", "approved", "singleUse"}:
        return False
    if approval != {"candidateId": candidate.get("candidateId"), "approved": True, "singleUse": True}:
        return False
    if not verify_attestation(candidate, evidence, attestation, key):
        return False
    return eligible(candidate, changes, evidence, observed_baseline)
