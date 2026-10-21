"""End-to-end run of both SpaceUI spike shells on the headless compositor.

Slow (about a minute per shell). Run inside `nix develop .#spaceui-spike`
after publishing Core and the CLI to out/:
    python3 -m unittest tests/spike/test_shells.py
"""
import json
import os
import shutil
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHOTS = ["01-launcher-idle", "02-core-open", "03-all-apps-orbit", "04-intent-suggestions",
         "05-core-responding", "06-core-reply", "07-app-snapped-launcher-planet",
         "08-launcher-hover-orbit", "09-pass-through"]


@unittest.skipUnless(shutil.which("sway") and os.environ.get("SPIKE_MESA"), "needs nix develop .#spaceui-spike")
class Shells(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run([sys.executable, str(ROOT / "scripts/spike/build_shared.py")], check=True)

    def scenario(self, kind):
        out = ROOT / f"out/spike/test-{kind}"
        subprocess.run([sys.executable, str(ROOT / "scripts/spike/harness.py"), kind, str(out)], check=True)
        m = json.loads((out / "metrics.json").read_text())
        for shot in SHOTS:
            self.assertTrue((out / f"{shot}.png").exists(), shot)
        self.assertFalse(m["exitedEarly"])
        self.assertLess(m["startupSeconds"], 30)
        self.assertTrue(m["inputPassThrough"]["ok"], m["inputPassThrough"])
        self.assertGreater(m["idleLauncher"]["fps"], 20)
        from PIL import Image
        before = Image.open(out / "02-core-open.png").convert("RGB")
        opened = Image.open(out / "03-all-apps-orbit.png").convert("RGB")
        reply = Image.open(out / "06-core-reply.png").convert("RGB")
        lay = json.loads((ROOT / "spike/spaceui/shared/layout.json").read_text())
        sx, sy = (1600 - lay["surface"]["width"]) // 2, 1000 - lay["surface"]["height"]
        # The Files slot (-125 degrees, radius 211) is empty before and filled after the core click.
        fx, fy = sx + lay["core"]["x"] - 121, sy + lay["core"]["y"] - 173
        self.assertNotEqual(before.getpixel((fx, fy)), opened.getpixel((fx, fy)))
        # Core's answer is drawn in the reply area, as dark text on the light desktop.
        row = [reply.getpixel((x, sy + lay["reply"]["y"] + 10)) for x in range(sx + 90, sx + 500)]
        self.assertTrue(any(sum(p) < 300 for p in row), "no reply text drawn")
        log = (out / "core.log").read_text()
        self.assertIn("POST", log)
        self.assertIn("/chat", log)

    def test_native_shell(self):
        self.scenario("native")

    def test_web_shell(self):
        self.scenario("web")


if __name__ == "__main__":
    unittest.main()
