#!/usr/bin/env python3
"""Fail-closed Guardian eligibility: candidate, verified statement and observed baseline must agree."""
from attestation import CHECKS
from verify_candidate import extract


def eligible(candidate, changes, statement, observed_baseline):
    """statement must already be signature-verified (attestation.verify)."""
    extract(candidate, changes)
    if not isinstance(statement, dict):
        return False
    if observed_baseline != candidate["baseline"] or statement["baseline"] != observed_baseline:
        return False
    if statement["candidateId"] != candidate["candidateId"]:
        return False
    return all(statement["checks"].get(name) is True for name in CHECKS)
