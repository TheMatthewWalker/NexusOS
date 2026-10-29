#!/usr/bin/env python3
"""Guardian staging service (ADR-002): stage a person's own approved decision, and report how it went.

The desktop's way to reach stage_executor.py without a root console. It is its
own root service and adds no power of its own: it stages only a decision the
broker recorded as permitted (which needs the person's approval and a valid
build attestation), only for the person who asked for it, and only once
(claim_for_staging). The running system is never switched; the change is used
from the next boot, as a one-time trial.

Protocol: the broker's (root_review_broker): length-framed JSON, callers
identified by SO_PEERCRED and limited to the broker's allowlist. Operations:

  stage    {decision}  -> {staged: true, toplevel, baseline}
  outcome  {decision}  -> {state: staged | applied | rolled-back | failed | unknown, at}

  serve --config /etc/nexus-guardian/config.json --bootloader systemd-boot --esp /boot
"""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import threading

from root_review_broker import Denied, serve
from stage_executor import BOOTLOADERS, ESP, StagingError, backup_hook, stage

DECISION = re.compile(r"[0-9a-f]{64}\Z")
# staging.jsonl events -> what the person is told.
OUTCOMES = {"staged": "staged", "confirmed": "applied", "rolled-back": "rolled-back", "fell-back": "rolled-back",
            "stage-failed": "failed", "stage-refused": "failed", "unexpected-generation": "failed"}


class StageService:
    def __init__(self, broker, *, bootloader="grub", esp=ESP, stager=stage, pre_stage=backup_hook):
        if bootloader not in BOOTLOADERS:
            raise ValueError("Unsupported bootloader")
        self.broker = broker
        self.allowed_uids = broker.allowed_uids
        self.bootloader, self.esp = bootloader, esp
        self.stager, self.pre_stage = stager, pre_stage
        self.lock = threading.Lock()  # One staging at a time.

    def _own_decision(self, uid, payload):
        if uid not in self.allowed_uids:
            raise Denied("Caller is not authorised")
        if not isinstance(payload, dict) or set(payload) != {"decision"}:
            raise Denied("Expected a decision")
        decision = payload["decision"]
        if not isinstance(decision, str) or DECISION.fullmatch(decision) is None:
            raise Denied("Invalid decision")
        owner = self.broker.decision_owner(decision)
        if owner is None or owner[0] != uid:
            raise Denied("No such decision for this caller")
        return decision, owner

    def stage(self, uid, payload):
        decision, (_, permitted, staged) = self._own_decision(uid, payload)
        if not permitted:
            raise Denied("Guardian did not approve this change")
        if staged:
            raise Denied("This change has already been staged")
        try:
            with self.lock:
                state = self.stager(self.broker, decision, pre_stage=self.pre_stage(), bootloader=self.bootloader,
                                    esp=self.esp)
        except Denied:
            raise
        except StagingError as error:
            raise Denied(str(error)) from None
        except Exception as error:  # noqa: BLE001 - the executor has already restored the baseline
            print(f"guardian-stage: staging {decision} failed: {error}", file=sys.stderr, flush=True)
            raise Denied("Staging failed; this computer still starts the current version") from None
        return {"staged": True, "toplevel": state["toplevel"], "baseline": state["baseline"]}

    def outcome(self, uid, payload):
        decision, _ = self._own_decision(uid, payload)
        state, at = "unknown", None
        log = Path(self.broker.state_dir) / "staging.jsonl"
        if log.exists():
            for line in log.read_text(encoding="utf-8").splitlines():
                try:
                    event = json.loads(line)
                except ValueError:
                    continue
                if event.get("decision") == decision and event.get("event") in OUTCOMES:
                    state, at = OUTCOMES[event["event"]], event.get("at")
        return {"decision": decision, "state": state, "at": at}

    OPERATIONS = ("stage", "outcome")

    def handle(self, uid, request):
        if not isinstance(request, dict) or set(request) != {"operation", "payload"}:
            raise Denied("Malformed request")
        if request["operation"] not in self.OPERATIONS:
            raise Denied("Unsupported operation")
        return getattr(self, request["operation"])(uid, request["payload"])


def main():
    from root_approval_issuer import build_broker, load_config
    import grp
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("operation", choices=("serve",))
    parser.add_argument("--config", type=Path, default=Path("/etc/nexus-guardian/config.json"))
    parser.add_argument("--socket", default="/run/nexus-guardian-stage/stage.sock")
    parser.add_argument("--bootloader", choices=BOOTLOADERS, default="grub")
    parser.add_argument("--esp", default=ESP)
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Root UID required")
    config = load_config(args.config)
    service = StageService(build_broker(config), bootloader=args.bootloader, esp=args.esp)
    serve(service, args.socket, group=grp.getgrnam(config["socketGroup"]).gr_gid, max_clients=4)


if __name__ == "__main__":
    main()
