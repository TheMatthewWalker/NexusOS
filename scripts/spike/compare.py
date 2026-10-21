#!/usr/bin/env python3
"""Run both spike shells through the harness and summarise the comparison.

    nix develop .#spaceui-spike --command python3 scripts/spike/compare.py [runs]

Each shell runs `runs` times (default 3) with motion and once with reduced
motion. Writes out/spike/summary.json with medians and the code size of each
shell. Everything runs on llvmpipe: compare the shells, not absolute numbers.
"""
import json
import statistics
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "out/spike"
SHELL_FILES = {
    "native": ["spike/spaceui/native/shell.qml", "spike/spaceui/native/PlasmaCore.qml"],
    "web": ["spike/spaceui/web/app.mjs", "spike/spaceui/web/index.html", "spike/spaceui/web/style.css",
            "spike/spaceui/web/host.py"],
}
SHARED = ["spike/spaceui/shared/suggest.mjs", "spike/spaceui/shared/plasma-core.glsl"]


def loc(paths):
    return sum(1 for p in paths for line in (ROOT / p).read_text().splitlines()
               if line.strip() and not line.strip().startswith(("//", "#", "<!--")))


def run(kind, out, reduced=False):
    cmd = [sys.executable, str(ROOT / "scripts/spike/harness.py"), kind, str(out)]
    if reduced:
        cmd.append("--reduced-motion")
    subprocess.run(cmd, check=True)
    return json.loads((out / "metrics.json").read_text())


def median(runs, *path):
    vals = []
    for r in runs:
        v = r
        for k in path:
            v = v.get(k) if isinstance(v, dict) else None
        if isinstance(v, (int, float)):
            vals.append(v)
    return round(statistics.median(vals), 1) if vals else None


def main():
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    summary = {"runs": n, "renderer": "llvmpipe (software GL, no GPU)", "sharedLinesOfCode": loc(SHARED), "shells": {}}
    for kind in ("native", "web"):
        runs = [run(kind, OUT / kind if i == 0 else OUT / f"{kind}-run{i + 1}") for i in range(n)]
        red = run(kind, OUT / f"{kind}-reduced", reduced=True)
        summary["shells"][kind] = {
            "linesOfCode": loc(SHELL_FILES[kind]),
            "startupSeconds": median(runs, "startupSeconds"),
            "idleLauncherPssMiB": median(runs, "idleLauncher", "pssMiB"),
            "idleLauncherCpuPercent": median(runs, "idleLauncher", "cpuPercent"),
            "idleLauncherFps": median(runs, "idleLauncher", "fps"),
            "respondingCpuPercent": median(runs, "responding", "cpuPercent"),
            "respondingFps": median(runs, "responding", "fps"),
            "respondingPssMiB": median(runs, "responding", "pssMiB"),
            "reducedMotionIdleCpuPercent": red["idleLauncher"]["cpuPercent"],
            "reducedMotionIdlePssMiB": red["idleLauncher"]["pssMiB"],
            "inputPassThrough": all(r.get("inputPassThrough", {}).get("ok") for r in runs + [red]),
            "allRunsCompleted": all("withAppOpen" in r and not r.get("exitedEarly") for r in runs + [red]),
        }
    (OUT / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
