#!/usr/bin/env python3
"""Download the Nexus image model once, carefully (nexus-image-model.service).

Started by nexus-image-model.timer a few minutes after boot and every 15
minutes after the last run finished; each run decides for itself whether to do
anything, so a failure never turns into a 7 GB download every few minutes:

  - the model is there and matches the pinned SHA-256: nothing to do;
  - not enough free space for the rest of the download plus a reserve
    (2 GiB): nothing is fetched, and the Images window says how much is needed;
  - a download stopped part-way: the next attempt resumes it (curl -C -);
  - after a failure the next attempt waits 10 minutes, then 20, 40... up to
    6 hours, and there are at most --max-per-day attempts in a (UTC) day;
  - once complete, the file is checked against the SHA-256 pinned in the Nix
    module (nexusos.imageModel.sha256); a mismatch deletes it. With no pinned
    hash, the host's published hash is used (Hugging Face sends the LFS
    SHA-256 as X-Linked-Etag), and with neither nothing is kept.

What it is doing is written to <dir>/download.json (readable by everyone) for
Core's /images/status: state, a plain message, the size, the attempts and the
time of the next try. Core works out progress from the .part file's size.
"""
import argparse
import calendar
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

STATE_FILE = "download.json"
RESERVE_BYTES = 2 * 1024 ** 3
BASE_BACKOFF = 10 * 60
MAX_BACKOFF = 6 * 3600
MAX_PER_DAY = 4
HASH = re.compile(r"[0-9a-f]{64}\Z")


def gb(count):
    return f"{count / 1000 ** 3:.1f} GB"


def free_bytes(path):
    info = os.statvfs(path)
    return info.f_bavail * info.f_frsize


def run(command):
    """Run curl; return (exit status, stdout)."""
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.stderr:
        print(result.stderr.strip()[-2000:], file=sys.stderr, flush=True)
    return result.returncode, result.stdout


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def published(url, runner):
    """The host's published (sha256, size) for url, either None if not given."""
    status, out = runner(["curl", "-sfIL", "--max-time", "60", url])
    if status != 0:
        return None, None
    sha = size = None
    for line in out.replace("\r", "").splitlines():
        name, _, value = line.partition(":")
        name, value = name.strip().lower(), value.strip().strip('"')
        if name == "x-linked-etag" and HASH.fullmatch(value.lower()):
            sha = value.lower()
        elif name in ("x-linked-size", "content-length") and value.isdigit():
            size = int(value) if name == "x-linked-size" or size is None else size
    return sha, size


class Downloader:
    def __init__(self, directory, file, url, sha256=None, size=None, *, max_per_day=MAX_PER_DAY,
                 reserve=RESERVE_BYTES, runner=run, clock=time.time, free=free_bytes):
        self.dir = Path(directory)
        self.model = self.dir / file
        self.part = self.dir / (file + ".part")
        self.url, self.size = url, size
        self.sha256 = sha256.lower() if sha256 else None
        if self.sha256 is not None and not HASH.fullmatch(self.sha256):
            raise ValueError("sha256 must be 64 hexadecimal characters")
        self.max_per_day, self.reserve = max_per_day, reserve
        self.runner, self.clock, self.free = runner, clock, free

    # --- the state file ---

    def load(self):
        try:
            state = json.loads((self.dir / STATE_FILE).read_text(encoding="utf-8"))
            return state if isinstance(state, dict) else {}
        except (OSError, ValueError):
            return {}

    def save(self, record, **fields):
        record.update(fields, updatedAt=int(self.clock()))
        path = self.dir / STATE_FILE
        temporary = path.with_suffix(".new")
        temporary.write_text(json.dumps(record, sort_keys=True), encoding="utf-8")
        os.chmod(temporary, 0o644)
        os.replace(temporary, path)
        return record

    def failed(self, state, message):
        """Count a failed attempt and choose when to try again."""
        failures = int(state.get("failures", 0)) + 1
        wait = min(BASE_BACKOFF * 2 ** (failures - 1), MAX_BACKOFF)
        print(message, file=sys.stderr, flush=True)
        self.save(state, state="waiting", reason="failed", message=message, failures=failures,
                  nextTry=int(self.clock()) + wait)
        return 1

    # --- one run ---

    def run_once(self):
        self.dir.mkdir(parents=True, exist_ok=True)
        state = self.load()
        now = int(self.clock())

        if self.model.exists():
            if self.sha256 is None or state.get("verified") == self.sha256:
                self.save(state, state="ready", reason="", message="The image model is ready.")
                return 0
            # A model from before the hash was pinned (or a different one): check it once.
            if sha256_of(self.model) == self.sha256:
                self.save(state, state="ready", reason="", message="The image model is ready.", verified=self.sha256)
                return 0
            self.model.unlink()
            print("The image model on disk does not match the pinned SHA-256; deleted it.", file=sys.stderr)

        if now < int(state.get("nextTry", 0)):
            return 0
        day = time.strftime("%Y-%m-%d", time.gmtime(now))
        if state.get("day") != day:
            state.update(day=day, attemptsToday=0)
        if int(state.get("attemptsToday", 0)) >= self.max_per_day:
            tomorrow = calendar.timegm(time.strptime(day, "%Y-%m-%d")) + 86400
            self.save(state, state="waiting", reason="daily-limit", nextTry=tomorrow,
                      message=f"The download failed {self.max_per_day} times today. Nexus tries again tomorrow.")
            return 0

        expected, total = self.sha256, self.size
        if expected is None or total is None:
            host_sha, host_size = published(self.url, self.runner)
            expected, total = expected or host_sha, total or host_size
        if expected is None:
            return self.failed(state, "There is no checksum for the image model, so it is not downloaded. "
                                      "Set nexusos.imageModel.sha256.")

        partial = self.part.stat().st_size if self.part.exists() else 0
        if total is not None and partial > total:
            self.part.unlink()
            partial = 0
        # Room for the rest of the file and a reserve, so the disk never fills up.
        needed = (total - partial if total is not None else 0) + self.reserve
        free = self.free(self.dir)
        if free < needed:
            self.save(state, state="waiting", reason="space", free=free, needed=needed, total=total, nextTry=0,
                      message=f"Not enough free disk space for the image model: {gb(free)} free, {gb(needed)} needed. "
                              "Free some space and the download starts by itself.")
            return 0

        state.update(attemptsToday=int(state.get("attemptsToday", 0)) + 1, attempts=int(state.get("attempts", 0)) + 1)
        self.save(state, state="downloading", reason="", total=total, startedAt=now, nextTry=0,
                  message="Downloading the image model." if partial == 0 else "Resuming the image model download.")
        status, _ = self.runner(["curl", "-fL", "--retry", "3", "--retry-delay", "30", "--speed-limit", "10240",
                                 "--speed-time", "300", "-C", "-", "-o", str(self.part), self.url])
        size = self.part.stat().st_size if self.part.exists() else 0
        if status != 0 or (total is not None and size != total):
            # Kept as it is: the next attempt resumes it.
            return self.failed(state, f"The download stopped part-way ({gb(size)} so far). It resumes later.")

        self.save(state, state="checking", message="Checking the downloaded image model.")
        actual = sha256_of(self.part)
        if actual != expected:
            self.part.unlink()
            return self.failed(state, f"The downloaded image model did not match its checksum (got {actual[:12]}…, "
                                      f"expected {expected[:12]}…), so it was deleted.")
        os.chmod(self.part, 0o644)
        os.replace(self.part, self.model)
        self.save(state, state="ready", reason="", message="The image model is ready.", verified=expected,
                  failures=0, nextTry=0)
        return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dir", required=True)
    parser.add_argument("--file", required=True)
    parser.add_argument("--url", required=True)
    parser.add_argument("--sha256", default="")
    parser.add_argument("--size", type=int, default=0)
    parser.add_argument("--max-per-day", type=int, default=MAX_PER_DAY)
    args = parser.parse_args(argv)
    return Downloader(args.dir, args.file, args.url, args.sha256 or None, args.size or None,
                      max_per_day=max(1, args.max_per_day)).run_once()


if __name__ == "__main__":
    sys.exit(main())
