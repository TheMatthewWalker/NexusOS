#!/usr/bin/env python3
"""Guardian's local builder (ADR-002): build, test and sign candidates on this computer.

A desktop has no separate build machine, so each NexusOS computer gets its own
Ed25519 builder key at first boot (`init-keys`: readable by root only; the
broker trusts its public half). The builder is its own root service. It never
decides and never changes the running system: for a change on the running
system it evaluates the host's own configuration with the candidate module
added, builds it, boots it once in a throwaway virtual machine
(candidate-build.nix), and signs a statement of exactly what it saw, with
today's value of each changed setting and package. The broker
still verifies the signature, baseline and store path, and a person must still
approve before anything is staged.

Protocol: the broker's (root_review_broker): length-framed JSON, callers
identified by SO_PEERCRED and limited to the broker's allowlist. Operations:

  build   {changes}       -> the candidate for that change on the running system,
                             and its job (started now, or the existing one)
  build   {update: {lockHash}} -> the same for the security update waiting
                             (system_update.py): the host configuration with no
                             overlay, evaluated from a copy of /etc/nixos with that
                             lock, read here from the root-owned update directory
  status  {candidateId}   -> {state, checks, attestation (once passed), message}
  updates {}              -> {status: the last update check, available: the update waiting or null}
  check-updates {}        -> asks for an update check now (nexus-update-check.path
                             starts the root service); at most every 10 minutes

The security update waiting is also built and tested without being asked
(prepare_waiting_update, every few minutes): by the time the person sees it,
it is ready to approve, whoever is logged in. A person's build request for it
then joins that job. Approving it still takes their password.

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
import shutil
import subprocess
import sys
import threading
import time

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.serialization import Encoding, NoEncryption, PrivateFormat

from attestation import CHECKS, public_raw, sign
from baseline_inspect import inspect
from candidate import prepare, prepare_update
from guardian_schema import DRV_PATH, ID_LENGTH, STORE_PATH, split
from root_review_broker import Denied, protected_directory, protected_file, serve
import system_update
from system_update import UpdateError

BUILD_FILE = Path(__file__).resolve().with_name("candidate-build.nix")
BUILDER_NAME = "this-computer"
MAX_QUEUED = 4
# Each checked system's result folder holds GC roots (the built system and its
# test machine), so old results are let go: after KEEP_SECONDS, and beyond the
# newest KEEP_RESULTS finished ones. A build is refused when the store has less
# than MIN_FREE_BYTES free (a NixOS system and its test machine are several GB).
KEEP_SECONDS = 14 * 24 * 3600
KEEP_RESULTS = 5
MIN_FREE_BYTES = system_update.MIN_FREE_BYTES
UNIT = re.compile(r"[A-Za-z0-9@._:-]{1,128}\Z")
NIX = ["nix", "--extra-experimental-features", "nix-command flakes"]
FINAL = ("passed", "failed")
CHECK_REQUEST = "update-requested"
CHECK_INTERVAL = 600
# How often the builder looks for an update waiting to be prepared.
PREPARE_INTERVAL = 300


# How qemu and Nix say the machine cannot run virtual machines.
KVM_MISSING = re.compile(r"(Could not access KVM kernel module|/dev/kvm|failed to initialize kvm|required system or feature not available.*kvm)", re.I)


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
                 vm_timeout=1800, start_worker=True, updates_dir=system_update.UPDATES_DIR,
                 host_flake_dir=system_update.HOST_FLAKE_DIR, free=system_update.free_bytes,
                 min_free=MIN_FREE_BYTES, keep_results=KEEP_RESULTS):
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
        self.owner = owner
        self.updates_dir, self.host_flake_dir = Path(updates_dir), Path(host_flake_dir)
        self.free, self.min_free = free, min_free
        self.keep_results = max(1, int(keep_results))
        self.lock = threading.Lock()
        self.queue = queue.Queue()
        self.jobs = {}
        self._load()
        self._clean_work()
        self._prune()
        if start_worker:
            threading.Thread(target=self._worker, daemon=True).start()
            threading.Thread(target=self._prepare_loop, daemon=True).start()

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
        remove_tree(self.results / candidate_id)

    def _prune(self):
        """Let go of finished results beyond the newest keep_results (and any past KEEP_SECONDS).

        Jobs still queued or running are never touched. Removing a result folder
        removes its GC roots, so the next garbage collection frees the space."""
        with self.lock:
            now = self.clock()
            finished = sorted((job for job in self.jobs.values() if job["state"] in FINAL),
                              key=lambda job: job.get("updatedAt", 0), reverse=True)
            for index, job in enumerate(finished):
                if index >= self.keep_results or now - job.get("updatedAt", 0) > KEEP_SECONDS:
                    self.jobs.pop(job["candidateId"], None)
                    self._forget(job["candidateId"])

    def _clean_work(self):
        """Remove what an interrupted run left in the work directory: result folders
        with no job, half-written job files, and copies of the host's flake."""
        for path in self.jobs_dir.iterdir():
            if path.suffix != ".json" or path.is_symlink() or not path.is_file():
                remove_tree(path)
        for path in self.results.iterdir():
            if path.name not in self.jobs:
                remove_tree(path)
            elif path.is_dir() and not path.is_symlink():
                remove_tree(path / "flake")

    def require_space(self):
        """Denied when the store has too little room to build and test a system."""
        free = self.free()
        if free < self.min_free:
            raise Denied(f"Not enough free disk space to check this change: {system_update.gib(free)} free, "
                         f"{system_update.gib(self.min_free)} needed. Free some space (Settings, Health shows what "
                         "takes room), then try again.")

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
        view["kind"] = job.get("kind", "change")
        if job.get("kind") == "update":
            view["update"] = job.get("updateSummary") or job["update"]
        return view

    # --- operations ---

    def build(self, uid, payload):
        self._authorise(uid)
        if not isinstance(payload, dict) or set(payload) not in ({"changes"}, {"update"}):
            raise Denied("build expects changes or update")
        observation = self.observer()
        if observation.get("verified") is not True:
            raise Denied("Running system could not be verified")
        if "changes" in payload:
            self.require_space()
        update = None
        try:
            if "update" in payload:
                update = self._waiting_update(payload["update"])
                candidate = prepare_update(update["lockHash"], observation["generationStorePath"], self.host_config)
            else:
                candidate = prepare(payload["changes"], observation["generationStorePath"], self.host_config)
        except ValueError as error:
            raise Denied(str(error)) from None
        return self._enqueue(candidate, uid, payload.get("changes"), update)

    def _enqueue(self, candidate, uid, changes, update):
        """The job for this candidate: the existing one (joined by uid), or a new one queued.

        uid None: the builder's own background preparation, which names no person."""
        candidate_id = candidate["candidateId"]
        with self.lock:
            job = self.jobs.get(candidate_id)
            if job is not None and job["state"] != "failed":
                # The same change on the same system: share the result (it names no person).
                if uid is not None and uid not in job["uids"]:
                    job["uids"].append(uid)
                    self._save(job)
                return {"candidate": candidate, "job": self.public_view(job)}
            active = sum(1 for other in self.jobs.values() if other["state"] not in FINAL)
            if active >= MAX_QUEUED:
                raise Denied("Guardian is already checking several changes; try again later")
            job = {"candidateId": candidate_id, "uids": [] if uid is None else [uid], "candidate": candidate, "changes": changes,
                   "state": "queued", "checks": {name: "pending" for name in CHECKS}, "message": "", "attestation": None,
                   "createdAt": int(self.clock())}
            if update is not None:
                job.update(kind="update", update=update)
            self.jobs[candidate_id] = job
            self._save(job)
        self.queue.put(candidate_id)
        return {"candidate": candidate, "job": self.public_view(job)}

    def prepare_waiting_update(self):
        """Build and test the security update waiting, before anyone asks: the candidate id, or None.

        Nothing when there is none, it is already being prepared or prepared,
        the store is short of room, or the running system cannot be verified.
        A failed preparation is not retried here (a person's request retries it)."""
        try:
            waiting = system_update.pending(self.updates_dir, self.host_flake_dir, owner=self.owner)
            if waiting is None:
                return None
            with self.lock:
                if any(job.get("kind") == "update" and job.get("update", {}).get("lockHash") == waiting["lockHash"]
                       for job in self.jobs.values()):
                    return None
            system_update.require_space(self.free(), self.min_free)
            observation = self.observer()
            if observation.get("verified") is not True:
                return None
            candidate = prepare_update(waiting["lockHash"], observation["generationStorePath"], self.host_config)
            return self._enqueue(candidate, None, None, waiting)["candidate"]["candidateId"]
        except (UpdateError, Denied, ValueError, OSError) as error:
            print(f"guardian-builder: could not prepare the waiting update: {error}", file=sys.stderr, flush=True)
            return None

    def _prepare_loop(self):
        while True:
            time.sleep(PREPARE_INTERVAL)
            self.prepare_waiting_update()

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

    # --- security updates ---

    def _waiting_update(self, update):
        """The update the root update check offers now, if it is the one named; with room to build it."""
        if not isinstance(update, dict) or set(update) != {"lockHash"}:
            raise Denied("update expects lockHash")
        waiting = system_update.pending(self.updates_dir, self.host_flake_dir, owner=self.owner)
        if waiting is None or waiting["lockHash"] != update["lockHash"]:
            raise Denied("This update is no longer the one waiting; check for updates again")
        try:
            system_update.require_space(self.free(), self.min_free)
        except UpdateError as error:
            raise Denied(str(error)) from None
        return waiting

    def updates(self, uid, payload):
        self._authorise(uid)
        if payload != {}:
            raise Denied("updates expects no arguments")
        try:
            available = system_update.pending(self.updates_dir, self.host_flake_dir, owner=self.owner)
        except UpdateError as error:
            print(f"guardian-builder: update waiting refused: {error}", file=sys.stderr, flush=True)
            available = None
        requested = self.work / CHECK_REQUEST
        return {"status": system_update.status(self.updates_dir, owner=self.owner), "available": available,
                "requestedAt": int(requested.stat().st_mtime) if requested.exists() else None}

    def check_updates(self, uid, payload):
        """Ask for an update check: a root path unit watches this file and starts nexus-update-check."""
        self._authorise(uid)
        if payload != {}:
            raise Denied("check-updates expects no arguments")
        requested = self.work / CHECK_REQUEST
        with self.lock:
            if requested.exists() and self.clock() - requested.stat().st_mtime < CHECK_INTERVAL:
                return {"requested": False, "message": "Guardian checked for updates a few minutes ago."}
            fd = os.open(requested, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "w", encoding="ascii") as stream:
                stream.write(f"{int(self.clock())} {uid}\n")
            os.utime(requested, (self.clock(), self.clock()))
        return {"requested": True, "message": "Checking for updates."}

    OPERATIONS = ("build", "status", "updates", "check-updates")

    def handle(self, uid, request):
        if not isinstance(request, dict) or set(request) != {"operation", "payload"}:
            raise Denied("Malformed request")
        if request["operation"] not in self.OPERATIONS:
            raise Denied("Unsupported operation")
        return getattr(self, request["operation"].replace("-", "_"))(uid, request["payload"])

    # --- the work ---

    def _update_flake(self, update, copy):
        """Copy /etc/nixos to copy and put the update's lock in it, or UpdateError.

        /etc/nixos/flake.lock must still be the lock the update was made from."""
        lock = system_update.read_lock(self.updates_dir, update["lockHash"], owner=self.owner)
        current = (self.host_flake_dir / "flake.lock").read_bytes()
        if system_update.sha256(current) != update["previousLockHash"]:
            raise UpdateError("This computer's configuration changed since the update was found; check for updates again.")
        shutil.rmtree(copy, ignore_errors=True)
        shutil.copytree(self.host_flake_dir, copy, symlinks=True)
        system_update.write_file(copy / "flake.lock", lock)
        # A move to another channel: flake.nix follows the lock's branch, as it will once installed.
        if (copy / "flake.nix").exists():
            text = (copy / "flake.nix").read_text()
            synced = system_update.sync_flake(text, lock)
            if synced != text:
                system_update.write_file(copy / "flake.nix", synced.encode(), 0o644)

    def _update_summary(self, update, baseline, toplevel):
        """The signed update record: the lock, the nixpkgs revisions and the package versions that change."""
        status, out, err = self.runner(NIX + ["store", "diff-closures", baseline, toplevel], 600)
        if status == 0:
            diff = system_update.diff_summary(out)
        else:
            print(f"guardian-builder: diff-closures failed: {err}", file=sys.stderr, flush=True)
            diff = {"packages": None, "otherPackages": 0, "added": 0, "removed": 0}
        summary = {"lockHash": update["lockHash"], "previousLockHash": update["previousLockHash"],
                   "nixpkgs": update["nixpkgs"], **diff}
        if update.get("nexusos") is not None:
            summary["nexusos"] = update["nexusos"]
        return summary

    def _worker(self):
        while True:
            self.run_job(self.queue.get())
            self._prune()

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
        update = job.get("update") if job.get("kind") == "update" else None
        folder = self.results / candidate_id
        folder.mkdir(mode=0o700, exist_ok=True)
        if update is None:
            module = folder / "candidate.nix"
            module.write_text(candidate["nixModule"], encoding="utf-8")
            module = str(module)
            options, packages = split(job["changes"])
        else:
            module, options, packages = "", {}, []
        # An update is evaluated from a copy of the host's flake with the update's lock.
        copy = folder / "flake"
        flake = self.flake if update is None else f"path:{copy}"
        arguments = ["--impure", "--file", str(self.build_file), "--argstr", "flake", flake,
                     "--argstr", "host", self.host, "--argstr", "module", module,
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

        # Space may have gone while the job waited in the queue.
        if update is None:
            try:
                self.require_space()
            except Denied as error:
                return fail("evaluate", str(error), "not enough free space")

        # The copy and its lock are made here, from /etc/nixos and the root-owned
        # update directory (never from a path anyone else names), checked against the lock's hash.
        if update is not None:
            try:
                system_update.require_space(self.free(), self.min_free)
                self._update_flake(update, copy)
            except (UpdateError, OSError) as error:
                shutil.rmtree(copy, ignore_errors=True)
                message = str(error) if isinstance(error, UpdateError) else "The update could not be prepared. Nothing changed."
                return fail("evaluate", message, str(error))

        # One evaluation gives both derivations; the builds then use them directly.
        self._update(job, state="evaluating", checks={"evaluate": "running"})
        status, out, err = self.runner(NIX + ["eval", "--json"] + arguments + ["evaluation"], 1800)
        # Evaluation copied the flake into the store; the work copy is not needed again.
        shutil.rmtree(copy, ignore_errors=True)
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
            if KVM_MISSING.search(err or ""):
                return fail("vm-test", "Guardian tests each change in a virtual machine, and this computer's virtualisation "
                            "(Intel VT-x or AMD-V) is turned off. Turn it on in the firmware (BIOS/UEFI) settings, "
                            "then try again. Nothing on this computer was changed.", err)
            return fail("vm-test", "The changed system did not start up in a test machine. Nothing on this computer was changed.", err)

        statement = {"attestationVersion": 1, "candidateId": candidate_id, "baseline": candidate["baseline"],
                     "toplevel": toplevel, "drvPath": drv_path, "builder": BUILDER_NAME,
                     "checks": {name: True for name in CHECKS}, "issuedAt": int(self.clock())}
        if update is None:
            # Signed, so the approval dialog can show today's values from the broker's record.
            statement["before"] = job["before"]
        else:
            # Signed too: the lock it was built with, and which package versions change.
            statement["update"] = self._update_summary(update, candidate["baseline"], toplevel)
            job["updateSummary"] = statement["update"]
        self._update(job, state="passed", checks={"vm-test": "pass"}, attestation=sign(statement, self.key),
                     message="Built and tested on this computer.")


def remove_tree(path):
    """Remove a file, link or folder without following links out of it."""
    path = Path(path)
    if path.is_symlink() or path.is_file():
        path.unlink(missing_ok=True)
    elif path.is_dir():
        shutil.rmtree(path, ignore_errors=True)


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
    parser.add_argument("--min-free-gib", type=int, default=MIN_FREE_BYTES // 1024 ** 3)
    parser.add_argument("--keep-results", type=int, default=KEEP_RESULTS)
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Root UID required")
    if args.operation == "init-keys":
        print(init_keys(args.dir, args.static_keys))
        return
    config = load_config(args.config)
    builder = LocalBuilder(args.dir, load_private_key(args.dir), resolve_uids(config["allowedUsers"]),
                           config["hostConfig"], required_units=args.require_unit, vm_timeout=args.vm_timeout,
                           min_free=args.min_free_gib * 1024 ** 3, keep_results=args.keep_results)
    serve(builder, args.socket, group=grp.getgrnam(config["socketGroup"]).gr_gid)


if __name__ == "__main__":
    main()
