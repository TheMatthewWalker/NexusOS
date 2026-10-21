#!/usr/bin/env python3
"""M5 inference benchmark through Nexus Core (real API, real engines).

For each provider it measures, via Core's authenticated Unix socket:
  - cold-start latency (first request after an unload)
  - warm latency (median of N further requests)
  - RAM / VRAM from /inference/telemetry: idle, loaded, and after unload
  - memory reclaimed by unload (loaded -> after-unload), never inferred

Run on each reference machine (CPU-only, 8 GB GPU, 24 GB GPU) with --profile
and attach the JSON to docs/evidence/M5.md. Numbers are observations of this
machine, not guarantees; missing GPU telemetry is reported as null.
"""
import argparse
import http.client
import json
import os
from pathlib import Path
import socket
import statistics
import sys
import time


class UnixHTTPConnection(http.client.HTTPConnection):
    def __init__(self, path, timeout=300):
        super().__init__("nexus", timeout=timeout)
        self.socket_path = path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self.socket_path)


class Core:
    def __init__(self, state_dir):
        state = Path(state_dir)
        self.socket = str(state / "nexus.sock")
        self.token = (state / "api-token").read_text().strip()

    def call(self, method, path, body=None):
        connection = UnixHTTPConnection(self.socket)
        headers = {"Authorization": "Bearer " + self.token}
        payload = None
        if body is not None:
            payload = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        connection.request(method, path, body=payload, headers=headers)
        response = connection.getresponse()
        data = response.read(4 * 1024 * 1024 + 1)
        connection.close()
        if len(data) > 4 * 1024 * 1024:
            raise ValueError("Response too large")
        return response.status, (json.loads(data) if data else None)


def used(snapshot, total_key, free_key):
    total, free = snapshot.get(total_key), snapshot.get(free_key)
    return None if total is None or free is None else total - free


def reclaimed(loaded, after, total_key, free_key):
    a, b = used(loaded, total_key, free_key), used(after, total_key, free_key)
    return None if a is None or b is None else a - b


def timed_chat(core, prompt):
    start = time.monotonic()
    status, body = core.call("POST", "/chat", {"prompt": prompt})
    elapsed = round(time.monotonic() - start, 3)
    if status != 200:
        raise RuntimeError(f"chat failed with HTTP {status}: {body}")
    return elapsed


def bench_provider(core, provider, runs, prompt, settle):
    status, _ = core.call("POST", "/providers/select", {"name": provider})
    if status != 200:
        raise RuntimeError(f"cannot select {provider}")
    core.call("POST", "/inference/unload")
    time.sleep(settle)
    idle = core.call("GET", "/inference/telemetry")[1]
    cold = timed_chat(core, prompt)
    warm = [timed_chat(core, prompt) for _ in range(runs)]
    loaded = core.call("GET", "/inference/telemetry")[1]
    unload_status, _ = core.call("POST", "/inference/unload")
    time.sleep(settle)
    after = core.call("GET", "/inference/telemetry")[1]
    return {
        "provider": provider,
        "coldStartSeconds": cold,
        "warmMedianSeconds": statistics.median(warm),
        "warmRuns": warm,
        "unloadAccepted": unload_status == 200,
        "telemetry": {"idle": idle, "loaded": loaded, "afterUnload": after},
        "ramReclaimedBytes": reclaimed(loaded, after, "ramTotalBytes", "ramAvailableBytes"),
        "vramReclaimedBytes": reclaimed(loaded, after, "vramTotalBytes", "vramFreeBytes"),
        "gpuSource": loaded.get("gpuSource"),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--state-dir", default=os.environ.get("NEXUS_STATE_DIR",
                        os.path.join(os.environ.get("XDG_STATE_HOME", os.path.expanduser("~/.local/state")), "nexusos")))
    parser.add_argument("--provider", action="append", choices=("local", "llama-cpp"), required=True)
    parser.add_argument("--profile", required=True, help="reference machine label, e.g. cpu-only, gpu-8gb, gpu-24gb")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--settle-seconds", type=float, default=3.0)
    parser.add_argument("--prompt", default="Explain Linux systemd in two sentences.")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if not 1 <= args.runs <= 20:
        parser.error("--runs must be 1-20")
    core = Core(args.state_dir)
    report = {"profile": args.profile, "host": " ".join(os.uname()[0:3:2]), "cpuCount": os.cpu_count(),
              "measuredAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "results": [bench_provider(core, p, args.runs, args.prompt, args.settle_seconds) for p in args.provider]}
    text = json.dumps(report, indent=2, sort_keys=True)
    if args.output:
        args.output.write_text(text + "\n")
    print(text)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError) as error:
        print(f"benchmark failed: {error}", file=sys.stderr)
        sys.exit(1)
