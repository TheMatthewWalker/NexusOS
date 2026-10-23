"""The core-vs-model GPU benchmark, end to end against a real Core and the mock model.

A fake nexus-shell-ipc stands in for the desktop shell and records how the
benchmark drives it. Needs Core published to out/ (as Core CI does):
    dotnet publish src/NexusOS.Core -c Release -o out/core
    python3 -m unittest tests/bench/test_core_gpu_bench.py
"""
import json
import os
import shutil
import socket
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOTNET = shutil.which("dotnet") or "/root/bin/dotnet"
BENCH = ROOT / "scripts/bench/nexus_core_gpu_bench.py"

FAKE_IPC = """#!/usr/bin/env python3
import json, sys
from pathlib import Path
state = Path(sys.argv[0]).with_name("state.json")
log = Path(sys.argv[0]).with_name("calls.log")
s = json.loads(state.read_text())
args = sys.argv[1:]
assert args[:2] == ["call", "nexus"], args
with log.open("a") as f:
    f.write(" ".join(args[2:]) + "\\n")
if args[2] == "state":
    print(json.dumps(s))
elif args[2] == "toggle":
    s["expanded"] = not s["expanded"]
elif args[2] == "simulateBusy":
    s["modelBusy"] = args[3] == "true"
state.write_text(json.dumps(s))
"""


@unittest.skipUnless((ROOT / "out/core/NexusOS.Core.dll").exists(), "publish Core to out/ first")
class CoreGpuBench(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = Path(tempfile.mkdtemp(prefix="nexus-gpu-bench-"))
        cls.state = cls.tmp / "state"
        cls.state.mkdir(mode=0o700)
        cls.bin = cls.tmp / "bin"
        cls.bin.mkdir()
        ipc = cls.bin / "nexus-shell-ipc"
        ipc.write_text(FAKE_IPC)
        ipc.chmod(0o755)
        (cls.bin / "state.json").write_text(json.dumps({"expanded": False, "modelBusy": False}))
        cls.env = dict(os.environ, NEXUS_STATE_DIR=str(cls.state), NEXUS_OLLAMA_URL="http://127.0.0.1:11435",
                       PATH=f"{cls.bin}:{os.environ['PATH']}")
        cls.env.pop("NEXUS_API_MODE", None)
        cls.mock = subprocess.Popen(["python3", str(ROOT / "scripts/mock-ollama.py")], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.core = subprocess.Popen([DOTNET, str(ROOT / "out/core/NexusOS.Core.dll")], env=cls.env, cwd=cls.state,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(120):
            if (cls.state / "nexus.sock").exists() and (cls.state / "api-token").exists():
                break
            time.sleep(0.5)
        else:
            raise RuntimeError("Core did not start")
        for _ in range(40):
            try:
                socket.create_connection(("127.0.0.1", 11435), 0.2).close()
                break
            except OSError:
                time.sleep(0.25)

    @classmethod
    def tearDownClass(cls):
        for p in (cls.core, cls.mock):
            p.terminate()
            p.wait(10)
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_measures_all_three_conditions_and_restores_the_shell(self):
        out = self.tmp / "report.json"
        run = subprocess.run(["python3", str(BENCH), "--profile", "test", "--runs", "2", "--output", str(out)],
                             env=self.env, capture_output=True, text=True, timeout=180)
        self.assertEqual(run.returncode, 0, run.stderr)
        report = json.loads(out.read_text())
        conditions = [r["condition"] for r in report["results"]]
        self.assertEqual(conditions, ["closed", "open-full", "open-busy"])
        for result in report["results"]:
            self.assertGreater(result["charsPerSecondMedian"], 0)
            self.assertEqual(len(result["charsPerSecond"]), 2)
            self.assertGreaterEqual(result["firstTextSecondsMedian"], 0)
        self.assertEqual(report["results"][0]["speedVsClosed"], 1.0)

        # It opened Nexus for the open conditions, held the busy budget only
        # for open-busy, and left the shell closed and not busy, as it found it.
        calls = (self.bin / "calls.log").read_text().split("\n")
        self.assertIn("toggle", calls)
        self.assertIn("simulateBusy true", calls)
        final = json.loads((self.bin / "state.json").read_text())
        self.assertEqual(final, {"expanded": False, "modelBusy": False})


if __name__ == "__main__":
    unittest.main()
