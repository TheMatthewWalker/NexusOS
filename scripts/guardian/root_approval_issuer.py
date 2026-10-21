#!/usr/bin/env python3
"""Root-only Guardian broker entry point: run the service, or approve a candidate at a root console.

  serve   --config /etc/nexus-guardian/config.json
  approve --config ... --candidate-id <id> --user <name>   (interactive root TTY only)

config.json: {"stateDir", "socket", "socketGroup", "allowedUsers": [...],
              "hostConfig": "<flake>#nixosConfigurations.<host>", "trustedKeys": "<path>"}
"""
import argparse
import grp
import json
import os
import pwd
import sys
from pathlib import Path

from root_review_broker import Denied, GuardianBroker, protected_directory, protected_file, serve

CONFIG_FIELDS = {"stateDir", "socket", "socketGroup", "allowedUsers", "hostConfig", "trustedKeys"}


def require_root_console(*, uid=None, stdin_tty=None, stdout_tty=None):
    uid = os.geteuid() if uid is None else uid
    stdin_tty = sys.stdin.isatty() if stdin_tty is None else stdin_tty
    stdout_tty = sys.stdout.isatty() if stdout_tty is None else stdout_tty
    if uid != 0 or not stdin_tty or not stdout_tty:
        raise PermissionError("Guardian approval requires an interactive root console")


def load_config(path, *, owner=0):
    protected_file(path, owner, allow_world_read=True)
    config = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(config, dict) or set(config) != CONFIG_FIELDS:
        raise ValueError("Guardian config has unexpected fields")
    if not isinstance(config["allowedUsers"], list) or not all(isinstance(u, str) for u in config["allowedUsers"]):
        raise ValueError("allowedUsers must be a list of user names")
    return config


def resolve_uids(names):
    # Root is never a broker client: approvals come from the console path instead.
    uids = {pwd.getpwnam(name).pw_uid for name in names}
    if 0 in uids:
        raise ValueError("root cannot be an allowed broker client")
    return uids


def build_broker(config, *, owner=0):
    return GuardianBroker(config["stateDir"], config["trustedKeys"], resolve_uids(config["allowedUsers"]),
                          config["hostConfig"], owner=owner)


def approve(broker, candidate_id, user, confirmation_reader=input, *, console_check=require_root_console):
    console_check()
    uid = pwd.getpwnam(user).pw_uid
    status = broker.status(uid, {"candidateId": candidate_id})
    candidate = status["candidate"]
    print(f"Candidate {candidate_id} submitted by {user} for baseline {candidate['baseline']}")
    print(f"Risk: {candidate['risk']['level']} {' '.join(candidate['risk']['notes'])}")
    print("Proposed NixOS overlay:\n" + candidate["nixModule"])
    if confirmation_reader(f"Type APPROVE {candidate_id} to approve once: ") != f"APPROVE {candidate_id}":
        raise PermissionError("Confirmation did not match; nothing approved")
    return broker.register_approval(candidate_id, uid)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("operation", choices=("serve", "approve"))
    parser.add_argument("--config", type=Path, default=Path("/etc/nexus-guardian/config.json"))
    parser.add_argument("--candidate-id")
    parser.add_argument("--user")
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Root UID required")
    config = load_config(args.config)
    protected_directory(config["stateDir"])
    broker = build_broker(config)
    if args.operation == "approve":
        if not args.candidate_id or not args.user:
            parser.error("approve requires --candidate-id and --user")
        try:
            approval = approve(broker, args.candidate_id, args.user)
        except (Denied, PermissionError, KeyError) as error:
            raise SystemExit(f"Not approved: {error}")
        print(f"Approved once for 120 seconds (approval {approval}).")
        return
    group = grp.getgrnam(config["socketGroup"]).gr_gid
    serve(broker, config["socket"], group=group)


if __name__ == "__main__":
    main()
