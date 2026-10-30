#!/usr/bin/env python3
"""Approve one Guardian candidate from the desktop, with the person's password (polkit).

  nexus-guardian-desktop-approve <candidateId> <buildHash>

The desktop alternative to `nexus-guardian approve` at a root console (ADR-002).
It is run only by polkit's exec helper for the action
org.nexusos.guardian.approve, which asks for an administrator's password every
time (auth_admin, never remembered) and then runs this as root, naming the
person who asked in PKEXEC_UID. Like the console path it registers ONE approval
for ONE candidate and ONE person, valid for 120 seconds and used up by the next
review; the person's own Core must then ask the broker to review it with a valid
build attestation, and the broker still decides. It cannot approve for root,
approves only a candidate that same person submitted, and changes nothing else.

The two arguments are what the person was shown: the dialog displays the
broker's own record of the candidate (describe), and polkit's prompt names this
command line, so both carry the candidate ID and build hash. After the password
this re-reads the candidate from the broker: it must exist, be the caller's,
not be staged yet, and its checked build must still be the one named here. The
approval is then bound to that candidate and build, so a different build (or
candidate) swapped in between the dialog and the password cannot use it.
"""
import json
import os
from pathlib import Path
import re
import sys
import syslog

from root_approval_issuer import build_broker, load_config
from root_review_broker import APPROVAL_SECONDS, Denied, protected_directory

CANDIDATE = re.compile(r"[0-9a-f]{24}\Z")
BUILD = re.compile(r"[0-9a-f]{64}\Z")
CONFIG = Path("/etc/nexus-guardian/config.json")


def requesting_uid(environ, euid):
    """The person polkit authorised, from PKEXEC_UID; never root."""
    if euid != 0:
        raise PermissionError("Must be run by polkit as root")
    value = environ.get("PKEXEC_UID", "")
    if not value.isdigit() or int(value) == 0:
        raise PermissionError("Must be started through polkit by a desktop user")
    return int(value)


def approve(broker, candidate_id, build, uid):
    if not isinstance(candidate_id, str) or CANDIDATE.fullmatch(candidate_id) is None:
        raise Denied("Invalid candidate ID")
    if not isinstance(build, str) or BUILD.fullmatch(build) is None:
        raise Denied("Invalid build hash")
    # The broker checks, in one transaction: allowlisted, submitted by uid, not
    # staged, and build is the one it verified for this candidate.
    return broker.register_approval(candidate_id, uid, build=build)


def main(argv=None, environ=None, euid=None):
    argv = sys.argv[1:] if argv is None else argv
    environ = os.environ if environ is None else environ
    euid = os.geteuid() if euid is None else euid
    try:
        uid = requesting_uid(environ, euid)
        if len(argv) != 2:
            raise Denied("Expected a candidate ID and a build hash")
        config = load_config(CONFIG)
        protected_directory(config["stateDir"])
        approve(build_broker(config), argv[0], argv[1], uid)
    except (Denied, PermissionError, ValueError, KeyError, OSError) as error:
        print(json.dumps({"approved": False, "error": str(error)}))
        return 1
    # The system journal records who approved what.
    syslog.openlog("nexus-guardian-desktop-approve", 0, syslog.LOG_AUTH)
    syslog.syslog(syslog.LOG_NOTICE, f"candidate {argv[0]} build {argv[1]} approved from the desktop by uid {uid}")
    print(json.dumps({"approved": True, "candidateId": argv[0], "buildHash": argv[1], "validSeconds": APPROVAL_SECONDS}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
