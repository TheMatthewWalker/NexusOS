#!/usr/bin/env python3
"""Guardian's local builder (ADR-002): build, test and sign candidates on this computer.

A desktop has no separate build machine, so each NexusOS computer gets its own
Ed25519 builder key at first boot (`init-keys`: readable by root only; the
broker trusts its public half). The builder is its own root service. It never
decides and never changes the running system: for a change on the running
system it evaluates the host's own configuration with the candidate module
added, builds it, boots it once in a throwaway virtual machine
(candidate-build.nix), and signs a statement of exactly what it saw. The broker
still verifies the signature, baseline and store path, and a person must still
approve before anything is staged.

Protocol: the broker's (root_review_broker): length-framed JSON, callers
identified by SO_PEERCRED and limited to the broker's allowlist. Operations:

  build   {changes}       -> the candidate for that change on the running system,
                             and its job (started now, or the existing one)
  status  {candidateId}   -> {state, checks, attestation (once passed), message}

Jobs run one at a time. A caller sees only jobs it asked for.

  serve      --config /etc/nexus-guardian/config.json --dir /var/lib/nexus-guardian-builder
  init-keys  --dir /var/lib/nexus-guardian-builder --static-keys /etc/nexus-guardian/trusted-keys
"""
import argparse
import base64
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import threading
import time

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.serialization import Encoding, NoEncryption, PrivateFormat

from attestation import CHECKS, public_raw, sign
from baseline_inspect import inspect
from candidate import prepare
from guardian_schema import DRV_PATH, ID_LENGTH, STORE_PATH, split
from root_review_broker import Denied, protected_directory, protected_file, serve

BUILD_FILE = Path(__file__).resolve().with_name("candidate-build.nix")
BUILDER_NAME = "this-computer"
MAX_QUEUED = 4
KEEP_SECONDS = 14 * 24 * 3600
UNIT = re.compile(r"[A-Za-z0-9@._:-]{1,128}\Z")
NIX = ["nix", "--extra-experimental-features", "nix-command flakes"]
FINAL = ("passed", "failed")


def run_command(command, timeout):
    """Run one nix command; return (exit status, stdout, last lines of stderr)."""
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        return 124, "", "timed out"
    return result.returncode, result.stdout, "\n".join(result.stderr.splitlines()[-30:])


def split_host_config(host_config):
    flake, attribute = host_config.split("#", 1)
    return flake, attribute.removeprefix("nixosConfigurations.")


# --- keys -----------------------------------------------------------------

def init_keys(directory, static_keys=None):
    """Create this computer's builder key once; (re)write the broker's trusted-keys file.

    Returns the public key. The private key is 0600 in a 0700 root directory and
    is never replaced; trusted-keys is the configured keys plus this one.
    """
    directory = Path(directory)
    directory.mkdir(mode=0o700, exist_ok=True)
    os.chmod(directory, 0o700)
    private = directory / "builder.key"
    if not private.exists():
        key = Ed25519PrivateKey.generate()
        fd = os.open(private, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "wb") as stream:
            stream.write(key.private_bytes(Encoding.Raw, PrivateFormat.Raw, NoEncryption()))
    protected_file(private, os.geteuid())
    key = Ed25519PrivateKey.from_private_bytes(private.read_bytes())
    public = base64.b64encode(public_raw(key)).decode("ascii")
    lines = []
    if static_keys is not None and Path(static_keys).exists():
        lines = [line.strip() for line in Path(static_keys).read_text(encoding="ascii").splitlines()
                 if line.strip() and not line.strip().startswith("#")]
    text = "# Written at boot by nexus-guardian-keys: configured keys, then this computer's builder.\n"
    text += "".join(line + "\n" for line in lines if line != public) + public + "\n"
    for name, content in (("builder.pub", public + "\n"), ("trusted-keys", text)):
        temporary = directory / (name + ".new")
        temporary.unlink(missing_ok=True)
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o644)
        with os.fdopen(fd, "w", encoding="ascii") as stream:
            stream.write(content)
        os.replace(temporary, directory / name)
    for name in ("work",):
        (directory / name).mkdir(mode=0o700, exist_ok=True)
    return public


# --- builder --------------------------------------------------------------

class LocalBuilder:
    def __init__(self, directory, private_key, allowed_uids, host_config, *, required_units=(),
                 observer=inspect, runner=run_command, clock=time.time, owner=0, build_file=BUILD_FILE,
                 vm_timeout=1800, start_worker=True):
        self.dir = Path(directory)
        self.work = self.dir / "work"
        protected_directory(self.work, owner)
        self.jobs_dir = self.work / "jobs"
        self.results = self.work / "results"
        for path in (self.jobs_dir, self.results):
            path.mkdir(mode=0o700, exist_ok=True)
        self.key = private_key
        self.allowed_uids = frozenset(allowed_uids)
        self.host_config = host_config
        self.flake, self.host = split_host_config(host_config)
        if not all(UNIT.fullmatch(unit) for unit in required_units):
            raise ValueError("Invalid required unit name")
        self.required_units = list(required_units)
        self.observer, self.runner, self.clock = observer, runner, clock
        self.build_file = Path(build_file)
        self.vm_timeout = int(vm_timeout)
        self.lock = threading.Lock()
        self.queue = queue.Queue()
        self.jobs = {}
        self._load()
        if start_worker:
            threading.Thread(target=self._worker, daemon=True).start()

    # Jobs are kept on disk so results survive a restart; one interrupted mid-way is failed.
    def _load(self):
        now = self.clock()
        for path in self.jobs_dir.glob("*.json"):
            try:
                job = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                path.unlink(missing_ok=True)
                continue
            if now - job.get("updatedAt", 0) > KEEP_SECONDS:
                self._forget(job["candidateId"])
                continue
            if job["state"] not in FINAL:
                job.update(state="failed", message="Checking was interrupted. Try again.")
                job["checks"] = {name: ("fail" if value == "running" else value) for name, value in job["checks"].items()}
                self._save(job)
            self.jobs[job["candidateId"]] = job

    def _forget(self, candidate_id):
        (self.jobs_dir / f"{candidate_id}.json").unlink(missing_ok=True)
        folder = self.results / candidate_id
        if folder.is_dir():
            for item in folder.iterdir():
                item.unlink()
            folder.rmdir()

    def _save(self, job):
        job["updatedAt"] = int(self.clock())
        path = self.jobs_dir / f"{job['candidateId']}.json"
        temporary = path.with_suffix(".new")
        temporary.unlink(missing_ok=True)
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(job, stream, sort_keys=True)
        os.replace(temporary, path)

    def _authorise(self, uid):
        if uid not in self.allowed_uids:
            raise Denied("Caller is not authorised")

    def public_view(self, job):
        view = {key: job[key] for key in ("candidateId", "state", "checks", "message")}
        view["attestation"] = job.get("attestation")
        view["before"] = job.get("before")
        return view

    # --- operations ---

    def build(self, uid, payload):
        self._authorise(uid)
        if not isinstance(payload, dict) or set(payload) != {"changes"}:
            raise Denied("build expects changes")
        observation = self.observer()
        if observation.get("verified") is not True:
            raise Denied("Running system could not be verified")
        try:
            candidate = prepare(payload["changes"], observation["generationStorePath"], self.host_config)
        except ValueError as error:
            raise Denied(str(error)) from None
        candidate_id = candidate["candidateId"]
        with self.lock:
            job = self.jobs.get(candidate_id)
            if job is not None and job["state"] != "failed":
                # The same change on the same system: share the result (it names no person).
                if uid not in job["uids"]:
                    job["uids"].append(uid)
                    self._save(job)
                return {"candidate": candidate, "job": self.public_view(job)}
            active = sum(1 for other in self.jobs.values() if other["state"] not in FINAL)
            if active >= MAX_QUEUED:
                raise Denied("Guardian is already checking several changes; try again later")
            job = {"candidateId": candidate_id, "uids": [uid], "candidate": candidate, "changes": payload["changes"], "state": "queued",
                   "checks": {name: "pending" for name in CHECKS}, "message": "", "attestation": None,
                   "createdAt": int(self.clock())}
            self.jobs[candidate_id] = job
            self._save(job)
        self.queue.put(candidate_id)
        return {"candidate": candidate, "job": self.public_view(job)}

    def status(self, uid, payload):
        self._authorise(uid)
        if not isinstance(payload, dict) or set(payload) != {"candidateId"}:
            raise Denied("status expects candidateId")
        candidate_id = payload["candidateId"]
        with self.lock:
            job = self.jobs.get(candidate_id) if isinstance(candidate_id, str) and len(candidate_id) == ID_LENGTH else None
            if job is None or uid not in job["uids"]:
                raise Denied("No such job for this caller")
            return self.public_view(job)

    OPERATIONS = ("build", "status")

    def handle(self, uid, request):
        if not isinstance(request, dict) or set(request) != {"operation", "payload"}:
            raise Denied("Malformed request")
        if request["operation"] not in self.OPERATIONS:
            raise Denied("Unsupported operation")
        return getattr(self, request["operation"])(uid, request["payload"])

    # --- the work ---

    def _worker(self):
        while True:
            self.run_job(self.queue.get())

    def _update(self, job, **fields):
        with self.lock:
            checks = fields.pop("checks", None)
            if checks:
                job["checks"].update(checks)
            job.update(fields)
            self._save(job)

    def run_job(self, candidate_id):
        job = self.jobs[candidate_id]
        if job["state"] != "queued":
            return
        candidate = job["candidate"]
        folder = self.results / candidate_id
        folder.mkdir(mode=0o700, exist_ok=True)
        module = folder / "candidate.nix"
        module.write_text(candidate["nixModule"], encoding="utf-8")
        options, packages = split(job["changes"])
        arguments = ["--impure", "--file", str(self.build_file), "--argstr", "flake", self.flake,
                     "--argstr", "host", self.host, "--argstr", "module", str(module),
                     "--argstr", "requiredUnits", " ".join(self.required_units),
                     "--argstr", "timeoutSeconds", str(self.vm_timeout),
                     "--argstr", "options", " ".join(sorted(options)), "--argstr", "packages", " ".join(packages)]

        def fail(check, message, detail):
            print(f"guardian-builder: {candidate_id} {check} failed: {detail}", file=sys.stderr, flush=True)
            self._update(job, state="failed", message=message, checks={check: "fail"})

        # The running system must still be the candidate's baseline.
        observation = self.observer()
        if observation.get("generationStorePath") != candidate["baseline"]:
            return fail("evaluate", "This computer changed since the change was proposed. Propose it again.", "baseline changed")

        # One evaluation gives both derivations; the builds then use them directly.
        self._update(job, state="evaluating", checks={"evaluate": "running"})
        status, out, err = self.runner(NIX + ["eval", "--json"] + arguments + ["evaluation"], 1800)
        try:
            derivations = json.loads(out)
            drv_path, vm_drv = derivations["toplevel"], derivations["vmTest"]
            before = clean_before(derivations.get("before"), options, packages)
        except (ValueError, TypeError, KeyError, AttributeError):
            drv_path = vm_drv = ""
        if status != 0 or DRV_PATH.fullmatch(drv_path) is None or DRV_PATH.fullmatch(vm_drv) is None:
            return fail("evaluate", "The change could not be put together with this computer's settings.", err)

        self._update(job, state="building", before=before, checks={"evaluate": "pass", "build": "running"})
        status, out, err = self.runner(NIX + ["build", "--print-out-paths", "--out-link", str(folder / "toplevel"),
                                              drv_path + "^out"], 4 * 3600)
        toplevel = out.strip().splitlines()[-1] if out.strip() else ""
        if status != 0 or STORE_PATH.fullmatch(toplevel) is None:
            return fail("build", "The change did not build. Nothing on this computer was changed.", err)

        self._update(job, state="testing", checks={"build": "pass", "vm-test": "running"})
        status, _, err = self.runner(NIX + ["build", "--out-link", str(folder / "vm-test"), vm_drv + "^out"],
                                     self.vm_timeout + 4 * 3600)
        if status != 0:
            return fail("vm-test", "The changed system did not start up in a test machine. Nothing on this computer was changed.", err)

        statement = {"attestationVersion": 1, "candidateId": candidate_id, "baseline": candidate["baseline"],
                     "toplevel": toplevel, "drvPath": drv_path, "builder": BUILDER_NAME,
                     "checks": {name: True for name in CHECKS}, "issuedAt": int(self.clock())}
        self._update(job, state="passed", checks={"vm-test": "pass"}, attestation=sign(statement, self.key),
                     message="Built and tested on this computer.")


def clean_before(before, options, packages):
    """Today's values from the evaluation, limited to the changed names and to booleans (or null)."""
    def pick(section, names):
        values = before.get(section) if isinstance(before, dict) and isinstance(before.get(section), dict) else {}
        return {name: values.get(name) if type(values.get(name)) is bool else None for name in names}
    return {"options": pick("options", sorted(options)), "packages": pick("packages", packages)}


def load_private_key(directory, owner=0):
    path = Path(directory) / "builder.key"
    protected_file(path, owner)
    return Ed25519PrivateKey.from_private_bytes(path.read_bytes())


def main():
    from root_approval_issuer import load_config, resolve_uids
    import grp
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("operation", choices=("serve", "init-keys"))
    parser.add_argument("--config", type=Path, default=Path("/etc/nexus-guardian/config.json"))
    parser.add_argument("--dir", type=Path, default=Path("/var/lib/nexus-guardian-builder"))
    parser.add_argument("--static-keys", type=Path)
    parser.add_argument("--socket", default="/run/nexus-guardian-builder/build.sock")
    parser.add_argument("--require-unit", action="append", default=[])
    parser.add_argument("--vm-timeout", type=int, default=1800)
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Root UID required")
    if args.operation == "init-keys":
        print(init_keys(args.dir, args.static_keys))
        return
    config = load_config(args.config)
    builder = LocalBuilder(args.dir, load_private_key(args.dir), resolve_uids(config["allowedUsers"]),
                           config["hostConfig"], required_units=args.require_unit, vm_timeout=args.vm_timeout)
    serve(builder, args.socket, group=grp.getgrnam(config["socketGroup"]).gr_gid)


if __name__ == "__main__":
    main()
