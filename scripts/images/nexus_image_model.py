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
                 reserve=RESERVE_BYTES, runner=run, clock=time.time, free=free_bytes, what="image model"):
        # What the messages call the file: the same script fetches the large chat model (--what).
        self.what = what
        # Written into the state file with everything else: which file was chosen, and why (resolve_hf).
        self.extra = {}
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
        if isinstance(fields.get("message"), str):
            fields["message"] = fields["message"].replace("image model", self.what)
        record.update(fields, updatedAt=int(self.clock()), **self.extra)
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


# --- a model named as on Hugging Face: repo and quantisation ("-hf owner/repo:Q8_0") ---

GIB = 1024 ** 3
OS_RESERVE = 4 * GIB          # memory left for the desktop and apps
GPU_RESERVE = 3 * GIB // 2    # graphics memory left for the desktop and the model's working memory


def memory_bytes(meminfo="/proc/meminfo"):
    try:
        for line in Path(meminfo).read_text(encoding="utf-8").splitlines():
            if line.startswith("MemTotal:"):
                return int(line.split()[1]) * 1024
    except (OSError, ValueError, IndexError):
        pass
    return 0


def vram_bytes(runner=run, drm="/sys/class/drm"):
    """The graphics card's own memory: NVIDIA's from nvidia-smi, AMD's from the kernel; 0 without one."""
    best = 0
    status, out = runner(["nvidia-smi", "--query-gpu=memory.total", "--format=csv,noheader,nounits"])
    if status == 0:
        best = max([int(v) * 1024 ** 2 for v in out.split() if v.isdigit()] or [0])
    for file in Path(drm).glob("card*/device/mem_info_vram_total"):
        try:
            best = max(best, int(file.read_text().strip()))
        except (OSError, ValueError):
            pass
    return best


def fit_bytes(memory, vram):
    """The biggest model file this computer can hold: in memory beside the desktop, plus what the card takes."""
    on_card = max(0, vram - GPU_RESERVE) if vram >= 3 * GIB else 0
    return max(0, memory - OS_RESERVE) + on_card


def api_json(url, runner):
    status, out = runner(["curl", "-sfL", "--max-time", "60", "-A", "llama-cpp", "-H", "Accept: application/json", url])
    if status != 0:
        return None
    try:
        return json.loads(out)
    except ValueError:
        return None


SAFE_PATH = re.compile(r"[A-Za-z0-9][A-Za-z0-9._+-]*(/[A-Za-z0-9][A-Za-z0-9._+-]*)*\.gguf\Z")
REPO = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*\Z")
QUANT = re.compile(r"[A-Za-z0-9_]{2,16}\Z")


def resolve_hf(repo, quant, fit, runner=run, allow_smaller=True):
    """Which file to fetch: {url, sha256, size, file, note} or {error}.

    The file the quantisation tag names (as llama.cpp's -hf resolves it, falling
    back to the tag in the file name). If it cannot fit this computer and
    allow_smaller is set, the biggest model file in the repository that does.
    Sizes and SHA-256s come from the repository's own file list.
    """
    if not REPO.fullmatch(repo) or not QUANT.fullmatch(quant):
        return {"error": "The model's repository or quantisation is not written correctly."}
    tree = api_json(f"https://huggingface.co/api/models/{repo}/tree/main?recursive=true", runner)
    if not isinstance(tree, list):
        return {"error": "Could not read the model's file list from Hugging Face. It is tried again later."}
    files = {}
    for entry in tree:
        if not isinstance(entry, dict) or entry.get("type") != "file":
            continue
        path, lfs = entry.get("path"), entry.get("lfs")
        if not isinstance(path, str) or not SAFE_PATH.fullmatch(path) or "mmproj" in path.lower() or not isinstance(lfs, dict):
            continue
        sha, size = str(lfs.get("oid", "")).lower(), lfs.get("size", entry.get("size"))
        if HASH.fullmatch(sha) and isinstance(size, int) and size > 0:
            files[path] = (sha, size)
    if not files:
        return {"error": "The model's repository lists no model files with checksums."}

    wanted = None
    manifest = api_json(f"https://huggingface.co/v2/{repo}/manifests/{quant}", runner)
    if isinstance(manifest, dict) and isinstance(manifest.get("ggufFile"), dict):
        name = manifest["ggufFile"].get("rfilename")
        wanted = name if name in files else None
    if wanted is None:
        tagged = sorted(p for p in files if re.search(r"(?<![A-Za-z0-9])" + re.escape(quant) + r"(?![A-Za-z0-9])", p, re.I))
        wanted = tagged[0] if tagged else None
    if wanted is None:
        return {"error": f"The model's repository has no {quant} file."}

    chosen, note = wanted, ""
    if fit and files[wanted][1] > fit:
        fitting = sorted((size, path) for path, (_, size) in files.items() if size <= fit)
        if not allow_smaller or not fitting:
            return {"error": f"{quant} is {gb(files[wanted][1])}, more than this computer can hold ({gb(fit)} of memory "
                             "and graphics memory to spare), and the repository has nothing smaller that fits."
                    if allow_smaller else
                    f"{quant} is {gb(files[wanted][1])}, more than this computer can hold ({gb(fit)} to spare)."}
        chosen = fitting[-1][1]
        note = (f"{quant} is {gb(files[wanted][1])}, more than this computer can hold ({gb(fit)} of memory and graphics "
                f"memory to spare), so the biggest version that fits is used: {Path(chosen).name} ({gb(files[chosen][1])}).")
    sha, size = files[chosen]
    return {"url": f"https://huggingface.co/{repo}/resolve/main/{chosen}", "sha256": sha, "size": size,
            "file": Path(chosen).name, "note": note}


def resolved_for(directory, repo, quant, allow_smaller, runner=run, memory=None, vram=None):
    """resolve_hf once, remembered in <dir>/resolved.json until the repository or quantisation asked for changes."""
    record = Path(directory) / "resolved.json"
    key = f"{repo}:{quant}:{int(allow_smaller)}"
    try:
        saved = json.loads(record.read_text(encoding="utf-8"))
        if saved.get("key") == key and HASH.fullmatch(saved.get("sha256", "")):
            return saved
    except (OSError, ValueError):
        pass
    fit = fit_bytes(memory if memory is not None else memory_bytes(), vram if vram is not None else vram_bytes(runner))
    found = resolve_hf(repo, quant, fit, runner, allow_smaller)
    if "error" not in found:
        found["key"] = key
        Path(directory).mkdir(parents=True, exist_ok=True)
        record.write_text(json.dumps(found, sort_keys=True), encoding="utf-8")
        os.chmod(record, 0o644)
    return found


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dir", required=True)
    parser.add_argument("--file", required=True)
    parser.add_argument("--url", default="")
    parser.add_argument("--hf-repo", default="", help="a Hugging Face repository of GGUF files, in place of --url")
    parser.add_argument("--hf-quant", default="", help="the quantisation wanted from --hf-repo, e.g. Q8_0")
    parser.add_argument("--exact", action="store_true", help="never a smaller version than --hf-quant, even if it cannot fit")
    parser.add_argument("--sha256", default="")
    parser.add_argument("--size", type=int, default=0)
    parser.add_argument("--max-per-day", type=int, default=MAX_PER_DAY)
    parser.add_argument("--what", default="image model", help="what the messages call the file")
    args = parser.parse_args(argv)
    url, sha256, size, extra = args.url, args.sha256 or None, args.size or None, {}
    if args.hf_repo:
        found = resolved_for(args.dir, args.hf_repo, args.hf_quant, not args.exact)
        if "error" in found:
            waiting = Downloader(args.dir, args.file, "unresolved", what=args.what)
            if (waiting.dir / args.file).exists():
                return 0
            Path(args.dir).mkdir(parents=True, exist_ok=True)
            state = waiting.load()
            if int(waiting.clock()) < int(state.get("nextTry", 0)):
                return 0
            return waiting.failed(state, found["error"])
        url, sha256, size = found["url"], found["sha256"], found["size"]
        extra = {"model": found["file"], "note": found["note"], "size": size}
    elif not url:
        parser.error("--url or --hf-repo is needed")
    downloader = Downloader(args.dir, args.file, url, sha256, size, max_per_day=max(1, args.max_per_day), what=args.what)
    downloader.extra = extra
    return downloader.run_once()


if __name__ == "__main__":
    sys.exit(main())
