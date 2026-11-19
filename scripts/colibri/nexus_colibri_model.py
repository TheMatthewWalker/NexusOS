#!/usr/bin/env python3
"""Downloads the very large model (colibri) into the person's own folder.

Core runs this as the person, when they turn the very large model on in
Settings, and again after a restart until the download is whole. It uses
colibri's own downloader (setup_download.py: resumable, each file checked
against the repository's SHA-256) and catalogue (setup_catalog.py: which
repository holds each model), from the colibri package NexusOS ships, so the
model always matches the engine.

  nexus-colibri-model --lib DIR --dir DIR --model ID     download, writing ID.status.json
  nexus-colibri-model --lib DIR --dir DIR --list         the models on offer, as JSON

Only the chat models with permissive licences are offered (MODELS). Exit
codes: 0 whole, 75 offline or the server failed (Core tries again later),
1 refused (does not fit, unknown model), 2 usage.
"""
import argparse
import json
import os
import shutil
import sys
import time

# What NexusOS offers, in the order shown. The catalogue (setup_catalog) has
# the repositories and sizes; these are the licences, checked by hand.
MODELS = {
    "qwen36-35b": {"licence": "Apache-2.0", "use": "Everyday questions, with thinking and tools"},
    "qwen3-coder-30b": {"licence": "Apache-2.0", "use": "Code, with tools"},
    "deepseek-v4-flash": {"licence": "MIT", "use": "A 284B model, read from the disk: slow, about a word a second"},
    "glm-5.2": {"licence": "MIT", "use": "A 744B model, read from the disk: very slow without lots of memory"},
}
GIB = 1 << 30
SPARE_GIB = 5          # left free on the disk after the download
EXIT_RETRY = 75


def memory_bytes():
    try:
        with open("/proc/meminfo") as f:
            for line in f:
                if line.startswith("MemTotal:"):
                    return int(line.split()[1]) * 1024
    except OSError:
        pass
    return 0


def free_bytes(path):
    while path and not os.path.isdir(path):
        path = os.path.dirname(path)
    return shutil.disk_usage(path or "/").free


def fit(entry, memory, free, present=0):
    """Whether this computer can hold the model: (ok, why not)."""
    need_disk = int(entry.disk_gb * 1e9) - present + SPARE_GIB * GIB
    if memory and memory < entry.ram_min_gb * GIB * 0.94:   # a "16 GB" computer reports about 15.6 GiB
        return False, f"needs {entry.ram_min_gb:g} GB of memory; this computer has {memory / GIB:.0f} GB"
    if free < need_disk:
        return False, f"needs {need_disk / 1e9:.0f} GB free on the disk; {free / 1e9:.0f} GB is free"
    return True, None


def write_status(path, **state):
    state["updated"] = int(time.time())
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(state, f)
    os.replace(tmp, path)


def offered(catalog, memory, free):
    rows = []
    for model_id, info in MODELS.items():
        entry = catalog.by_id(model_id)
        if entry is None:
            continue
        ok, why = fit(entry, memory, free)
        rows.append({"id": model_id, "name": entry.name, "use": info["use"], "licence": info["licence"],
                     "downloadGb": round(entry.disk_gb, 1), "memoryMinGb": entry.ram_min_gb,
                     "memoryGoodGb": entry.ram_good_gb, "streams": entry.size_class == "large",
                     "fits": ok, "why": why})
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser(prog="nexus-colibri-model")
    ap.add_argument("--lib", required=True, help="colibri's lib/colibri folder")
    ap.add_argument("--dir", required=True, help="where models are kept")
    ap.add_argument("--model")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args(argv)
    sys.path.insert(0, args.lib)
    import setup_catalog as catalog      # noqa: E402  (from the colibri package)
    import setup_download as download    # noqa: E402

    os.makedirs(args.dir, mode=0o700, exist_ok=True)
    memory = memory_bytes()
    if args.list:
        json.dump(offered(catalog, memory, free_bytes(args.dir)), sys.stdout)
        return 0
    if args.model not in MODELS or catalog.by_id(args.model) is None:
        print(f"unknown model {args.model!r}; offered: {', '.join(MODELS)}", file=sys.stderr)
        return 2
    entry = catalog.by_id(args.model)
    model_dir = os.path.join(args.dir, args.model)
    status = os.path.join(args.dir, args.model + ".status.json")
    base = {"model": args.model, "name": entry.name}

    if download.is_complete(model_dir):
        write_status(status, state="ready", message=f"{entry.name} is downloaded.", **base)
        return 0
    write_status(status, state="checking", message="Looking up the model's files.", **base)
    try:
        files = download.list_repo_files(entry.repo, entry.revision, include=entry.include, exclude=entry.exclude)
    except download.DownloadError as error:
        write_status(status, state="waiting", message=f"Could not reach Hugging Face ({error}). Trying again later.", **base)
        return EXIT_RETRY
    total = sum(int(f["size"]) for f in files)
    present = download.bytes_present(model_dir, files)
    ok, why = fit(entry, memory, free_bytes(args.dir), present)
    if not ok:
        write_status(status, state="too-big", message=f"{entry.name} {why}.", total=total, done=present, **base)
        return 1

    last = [0.0]

    def progress(event):
        now = time.monotonic()
        if now - last[0] < 1 and event["done"] < event["total"]:
            return
        last[0] = now
        write_status(status, state="downloading", message=f"Downloading {entry.name}.", done=event["done"],
                     total=event["total"], percent=int(event["done"] * 100 // max(event["total"], 1)), **base)

    try:
        download.download_repo(entry.repo, entry.revision, model_dir, files, progress=progress)
    except (download.DownloadError, OSError) as error:
        done = download.bytes_present(model_dir, files)
        write_status(status, state="waiting", message=f"The download stopped ({error}). It goes on from where it stopped.",
                     done=done, total=total, percent=int(done * 100 // max(total, 1)), **base)
        return EXIT_RETRY
    write_status(status, state="ready", message=f"{entry.name} is downloaded.", done=total, total=total, percent=100, **base)
    return 0


if __name__ == "__main__":
    sys.exit(main())
