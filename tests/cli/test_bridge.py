"""End-to-end: the nexus CLI's streaming ask and bridge mode against a real Core.

Needs Core and the CLI published to out/ (as Core CI does) and the mock model:
    dotnet publish src/NexusOS.Core -c Release -o out/core
    dotnet publish src/NexusOS.Cli -c Release -o out/cli
    python3 -m unittest tests/cli/test_bridge.py
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


@unittest.skipUnless((ROOT / "out/core/NexusOS.Core.dll").exists() and (ROOT / "out/cli/NexusOS.Cli.dll").exists(),
                     "publish Core and the CLI to out/ first")
class Bridge(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.state = tempfile.mkdtemp(prefix="nexus-bridge-")
        os.chmod(cls.state, 0o700)
        cls.env = dict(os.environ, NEXUS_STATE_DIR=cls.state, NEXUS_OLLAMA_URL="http://127.0.0.1:11435")
        cls.env.pop("NEXUS_API_MODE", None)
        cls.mock = subprocess.Popen(["python3", str(ROOT / "scripts/mock-ollama.py")], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.core = subprocess.Popen([DOTNET, str(ROOT / "out/core/NexusOS.Core.dll")], env=cls.env, cwd=cls.state,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(120):
            if cls.cli("health").returncode == 0:
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
        shutil.rmtree(cls.state, ignore_errors=True)

    @classmethod
    def cli(cls, *args, **kw):
        return subprocess.run([DOTNET, str(ROOT / "out/cli/NexusOS.Cli.dll"), *args], env=cls.env,
                              capture_output=True, text=True, timeout=60, **kw)

    def test_ask_stream_prints_the_whole_answer(self):
        run = self.cli("ask", "--stream", "hello", "there")
        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertEqual(run.stdout.strip(), "Nexus mock response: hello there")

    def test_bridge_streams_tagged_events_and_answers_other_ops(self):
        bridge = subprocess.Popen([DOTNET, str(ROOT / "out/cli/NexusOS.Cli.dll"), "bridge"], env=self.env,
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        try:
            self.assertEqual(json.loads(bridge.stdout.readline())["type"], "ready")
            for req in ({"id": 1, "op": "chat", "prompt": "plan my research"},
                        {"id": "s", "op": "suggest", "prompt": "plan my research"},
                        {"id": 3, "op": "apps"},
                        {"id": 4, "op": "nope"}):
                bridge.stdin.write(json.dumps(req) + "\n")
            bridge.stdin.flush()
            events = []
            while len([e for e in events if e.get("type") in ("done", "error", "suggestions", "apps")]) < 4:
                line = bridge.stdout.readline()
                self.assertTrue(line, "bridge closed early")
                events.append(json.loads(line))
        finally:
            bridge.stdin.close()
            bridge.wait(10)
            bridge.stdout.close()
        chat = [e for e in events if e.get("id") == 1]
        self.assertGreater(len([e for e in chat if e["type"] == "delta"]), 2, "the answer should arrive in pieces")
        self.assertEqual("".join(e["text"] for e in chat if e["type"] == "delta"), "Nexus mock response: plan my research")
        self.assertEqual(chat[-1], {"type": "done", "provider": "local", "id": 1})
        suggest = next(e for e in events if e.get("id") == "s")
        self.assertEqual(suggest["data"]["source"], "rules")
        self.assertEqual([a["id"] for a in suggest["data"]["apps"]], ["browser", "files"])
        apps = next(e for e in events if e.get("id") == 3)
        self.assertEqual(len(apps["data"]), 6)
        unknown = next(e for e in events if e.get("id") == 4)
        self.assertEqual(unknown["type"], "error")

    def test_bridge_unload_frees_the_model_and_reports_measured_vram(self):
        bridge = subprocess.Popen([DOTNET, str(ROOT / "out/cli/NexusOS.Cli.dll"), "bridge"], env=self.env,
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        try:
            self.assertEqual(json.loads(bridge.stdout.readline())["type"], "ready")
            bridge.stdin.write(json.dumps({"id": 7, "op": "unload"}) + "\n")
            bridge.stdin.flush()
            event = json.loads(bridge.stdout.readline())
        finally:
            bridge.stdin.close()
            bridge.wait(10)
            bridge.stdout.close()
        self.assertEqual(event["type"], "unloaded", event)
        self.assertEqual(event["id"], 7)
        freed = event["data"]["vramFreedBytes"]
        # Without a readable GPU the amount is unknown, never guessed.
        self.assertTrue(freed is None or (isinstance(freed, int) and freed > 0), freed)
        if not shutil.which("nvidia-smi"):
            self.assertIsNone(freed)


if __name__ == "__main__":
    unittest.main()
