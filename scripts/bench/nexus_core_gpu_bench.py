#!/usr/bin/env python3
"""Does the Nova core slow the local model down? A/B benchmark on a real desktop.

Run it inside your NexusOS session, with the local model loaded on the GPU:

    python3 scripts/bench/nexus_core_gpu_bench.py --profile my-gpu --output core-gpu.json

It streams the same answer several times through Nexus Core (/chat/stream)
in three conditions, switching the shell between them over its IPC:

  closed      Nexus closed: only the small launcher core is on screen
  open-full   Nexus open, the core drawing at full rate and detail
  open-busy   Nexus open, the core in its "model is busy" budget
              (at most 30 fps and 32 samples per pixel)

For each condition it reports streamed characters per second (median),
time to the first streamed text, and GPU busy % sampled during the runs
(nvidia-smi, or the amdgpu gpu_busy_percent file; null when neither exists).
Compare open-full and open-busy with closed to see what the core costs.
"""
import argparse
import glob
import json
import os
from pathlib import Path
import shutil
import statistics
import subprocess
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
from nexus_inference_bench import Core, UnixHTTPConnection  # noqa: E402

PROMPT = "List the numbers from 1 to 120 in words, separated by commas, and nothing else."


def shell(*args):
    run = subprocess.run(["nexus-shell-ipc", "call", "nexus", *args], capture_output=True, text=True, timeout=20)
    if run.returncode != 0:
        raise RuntimeError(f"nexus-shell-ipc {' '.join(args)} failed: {run.stderr.strip()}")
    return run.stdout.strip()


def shell_state():
    return json.loads(shell("state"))


def set_condition(name):
    state = shell_state()
    want_open = name != "closed"
    if state["expanded"] != want_open:
        shell("toggle")
    shell("simulateBusy", "true" if name == "open-busy" else "false")
    time.sleep(2)
    state = shell_state()
    if state["expanded"] != want_open or state["modelBusy"] != (name == "open-busy"):
        raise RuntimeError(f"shell did not reach {name}: {state}")


class GpuSampler:
    """Samples GPU busy % every 0.5 s in the background."""

    def __init__(self):
        self.amd = glob.glob("/sys/class/drm/card*/device/gpu_busy_percent")
        self.nvidia = shutil.which("nvidia-smi")
        self.samples, self._stop = [], threading.Event()

    def read(self):
        if self.nvidia:
            out = subprocess.run([self.nvidia, "--query-gpu=utilization.gpu", "--format=csv,noheader,nounits"],
                                 capture_output=True, text=True, timeout=5).stdout.split()
            return float(out[0]) if out else None
        if self.amd:
            return float(Path(self.amd[0]).read_text().strip())
        return None

    def __enter__(self):
        def loop():
            while not self._stop.wait(0.5):
                value = self.read()
                if value is not None:
                    self.samples.append(value)
        self._thread = threading.Thread(target=loop, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        self._thread.join()

    def mean(self):
        return round(statistics.mean(self.samples), 1) if self.samples else None


def streamed_chat(core, prompt):
    """Returns (seconds to first text, characters, seconds of streaming)."""
    connection = UnixHTTPConnection(core.socket)
    body = json.dumps({"prompt": prompt}).encode()
    start = time.monotonic()
    connection.request("POST", "/chat/stream", body=body, headers={
        "Authorization": "Bearer " + core.token, "Content-Type": "application/json"})
    response = connection.getresponse()
    if response.status != 200:
        raise RuntimeError(f"/chat/stream returned HTTP {response.status}")
    first, chars = None, 0
    while True:
        line = response.readline(1024 * 1024)
        if not line:
            break
        event = json.loads(line)
        if event.get("type") == "delta":
            if first is None:
                first = time.monotonic()
            chars += len(event.get("text", ""))
        elif event.get("type") == "error":
            raise RuntimeError(f"model error: {event.get('message')}")
        elif event.get("type") == "done":
            break
    end = time.monotonic()
    connection.close()
    if first is None:
        raise RuntimeError("the model streamed no text")
    return first - start, chars, max(end - first, 1e-6)


def run_condition(core, name, runs, prompt):
    set_condition(name)
    streamed_chat(core, prompt)                       # warm-up, not counted
    rates, firsts = [], []
    with GpuSampler() as gpu:
        for _ in range(runs):
            first, chars, seconds = streamed_chat(core, prompt)
            rates.append(chars / seconds)
            firsts.append(first)
    return {"condition": name, "charsPerSecondMedian": round(statistics.median(rates), 1),
            "charsPerSecond": [round(r, 1) for r in rates],
            "firstTextSecondsMedian": round(statistics.median(firsts), 3),
            "gpuBusyPercentMean": gpu.mean()}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--state-dir", default=os.environ.get("NEXUS_STATE_DIR",
                        os.path.join(os.environ.get("XDG_STATE_HOME", os.path.expanduser("~/.local/state")), "nexusos")))
    parser.add_argument("--profile", required=True, help="a label for this machine, e.g. rx7800-16gb")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--prompt", default=PROMPT)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if not 1 <= args.runs <= 20:
        parser.error("--runs must be 1-20")
    core = Core(args.state_dir)
    start_state = shell_state()
    try:
        results = [run_condition(core, name, args.runs, args.prompt) for name in ("closed", "open-full", "open-busy")]
    finally:
        shell("simulateBusy", "false")
        if shell_state()["expanded"] != start_state["expanded"]:
            shell("toggle")
    base = results[0]["charsPerSecondMedian"]
    for result in results:
        result["speedVsClosed"] = round(result["charsPerSecondMedian"] / base, 3) if base else None
    report = {"profile": args.profile, "measuredAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "coreSteps": os.environ.get("NEXUS_CORE_STEPS"), "results": results}
    text = json.dumps(report, indent=2, sort_keys=True)
    if args.output:
        args.output.write_text(text + "\n")
    print(text)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        print(f"benchmark failed: {error}", file=sys.stderr)
        sys.exit(1)
