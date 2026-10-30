#!/usr/bin/env python3
"""Fail-closed Guardian eligibility: candidate, verified statement and observed baseline must agree."""
from attestation import CHECKS
from verify_candidate import extract, extract_update


def eligible(candidate, changes, statement, observed_baseline, update=None):
    """statement must already be signature-verified (attestation.verify).

    A change (changes) is attested without an update record; an update
    (update: {lockHash}) only by a statement whose update names that same lock.
    """
    if update is None:
        extract(candidate, changes)
    else:
        lock_hash = extract_update(candidate, update)
    if not isinstance(statement, dict):
        return False
    if update is None and "update" in statement:
        return False
    if update is not None and (not isinstance(statement.get("update"), dict) or statement["update"].get("lockHash") != lock_hash):
        return False
    if observed_baseline != candidate["baseline"] or statement["baseline"] != observed_baseline:
        return False
    if statement["candidateId"] != candidate["candidateId"]:
        return False
    return all(statement["checks"].get(name) is True for name in CHECKS)
