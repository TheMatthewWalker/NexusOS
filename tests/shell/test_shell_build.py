"""Checks for the Nexus shell build (shell/tools/build.py).

Run inside `nix develop .#desktop`: python3 -m unittest tests/shell/test_shell_build.py
"""
import importlib.util
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHELL = ROOT / "shell"


def load_build():
    spec = importlib.util.spec_from_file_location("shell_build", SHELL / "tools/build.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ShellBuild(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = Path(tempfile.mkdtemp(prefix="nexus-shell-"))
        cls.out = cls.tmp / "shell"
        args = ["python3", str(SHELL / "tools/build.py"), "--out", str(cls.out)]
        if not shutil.which("qsb"):
            args.append("--no-qsb")
        subprocess.run(args, check=True)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_every_qml_file_and_generated_input_is_present(self):
        for name in ["shell.qml", "NovaCore.qml", "CoreBridge.qml", "AppDirectory.qml", "Wallpaper.qml",
                     "SystemPanel.qml", "lib/orbit.mjs", "lib/tokens.mjs", "lib/nova.frag", "lib/nova-noise-atlas.png"]:
            self.assertTrue((self.out / name).exists(), name)

    def test_every_referenced_icon_exists(self):
        icons = set()
        for qml in self.out.glob("*.qml"):
            icons |= set(re.findall(r'"icons/([a-z-]+)\.svg"', qml.read_text()))
            # Roles and planets build icon paths from ids: "icons/" + id + ".svg".
        icons |= {"files", "browser", "terminal", "studio", "models", "settings",
                  "network", "power", "bluetooth", "privacy", "time", "display", "sound"}
        for icon in icons:
            self.assertTrue((self.out / "icons" / f"{icon}.svg").exists(), icon)

    def test_nova_shader_is_valid_vulkan_glsl(self):
        tool = shutil.which("glslangValidator")
        if not tool:
            self.skipTest("glslangValidator not installed")
        frag = self.out / "lib/nova.frag"
        run = subprocess.run([tool, "-V", "-o", "/dev/null", str(frag)], capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertIn((SHELL / "qml/lib/nova-core.glsl").read_text(), frag.read_text())

    def test_noise_atlas_packs_the_study_field(self):
        from PIL import Image
        vol = load_build().noise_volume()
        atlas = Image.open(self.out / "lib/nova-noise-atlas.png")
        self.assertEqual((atlas.size, atlas.mode), ((528, 528), "L"))
        for x, y, z in [(0, 0, 0), (5, 9, 13), (63, 63, 63), (31, 2, 40)]:
            tx, ty = (z % 8) * 66, (z // 8) * 66
            self.assertEqual(atlas.getpixel((tx + 1 + x, ty + 1 + y)), vol[x + 64 * y + 4096 * z])
        self.assertEqual(atlas.getpixel((0, 1)), vol[63])
        study = (ROOT / "docs/design/spaceui/studies/nexus-core-nova.html").read_text()
        self.assertIn("seed * 1664525 + 1013904223", study)

    def test_suggestions_are_not_in_the_shell(self):
        """Keyword rules live in Core (POST /suggest); the shell must not carry its own."""
        orbit = (self.out / "lib/orbit.mjs").read_text()
        self.assertNotIn("suggestApps", orbit)
        self.assertNotIn("PATTERNS", orbit)

    def test_shell_safety_rules_hold_in_the_source(self):
        shell = (self.out / "shell.qml").read_text()
        self.assertIn("WlrLayer.Top", shell)
        self.assertNotIn("WlrLayer.Overlay", shell)              # never above lock screens
        self.assertIn("textFormat: Text.PlainText", shell)       # model output is plain text
        self.assertIn("Not installed", shell)                    # missing apps are not faked
        self.assertIn("Offline", shell)

    def test_core_gives_the_gpu_back_to_the_model(self):
        """The local model shares the GPU: the core must throttle and pause."""
        nova = (self.out / "NovaCore.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        self.assertIn("running: core.visible && !core.reduced && !core.paused", nova)
        self.assertIn("core.busy ? Math.min(core.steps, core.busySteps) : core.steps", nova)
        self.assertIn("const cap = core.busy ? core.busyFps", nova)
        self.assertIn("busy: root.modelBusy", shell)
        self.assertIn("paused: root.launcherPaused", shell)
        self.assertIn("IdleMonitor", shell)

    def test_orbit_maths(self):
        if not shutil.which("node"):
            self.skipTest("node not installed")
        test = self.out / "lib/orbit.test.mjs"
        shutil.copy(ROOT / "tests/shell/orbit.test.mjs", test)
        run = subprocess.run(["node", "--test", str(test)], capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)


if __name__ == "__main__":
    unittest.main()
