"""Checks for the SpaceUI spike's shared inputs (scripts/spike/build_shared.py).

Run inside `nix develop .#spaceui-spike`: python3 -m unittest tests/spike/test_shared.py
"""
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPIKE = ROOT / "spike/spaceui"


class SharedInputs(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(["python3", str(ROOT / "scripts/spike/build_shared.py"), "--no-qsb"], check=True)

    def validate(self, src, stage_suffix, *extra):
        tool = shutil.which("glslangValidator")
        if not tool:
            self.skipTest("glslangValidator not installed")
        tmp = SPIKE / f"web/.check.{stage_suffix}"
        tmp.write_text(src)
        try:
            run = subprocess.run([tool, *extra, str(tmp)], capture_output=True, text=True)
        finally:
            tmp.unlink()
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    def test_webgl_shader_is_valid_glsl_es_300(self):
        text = (SPIKE / "web/plasma-shader.mjs").read_text()
        src = subprocess.run(["node", "--input-type=module", "-e",
                              f"import {{ fragmentSource }} from '{SPIKE}/web/plasma-shader.mjs';"
                              "process.stdout.write(fragmentSource)"],
                             capture_output=True, text=True, check=True).stdout
        self.assertTrue(src.startswith("#version 300 es"))
        self.assertIn("nexusPlasma", text)
        self.validate(src, "frag")

    def test_qt_shader_is_valid_vulkan_glsl(self):
        self.validate((SPIKE / "native/plasma.frag").read_text(), "frag", "-V", "-o", "/dev/null")

    def test_both_wrappers_share_one_body(self):
        body = (SPIKE / "shared/plasma-core.glsl").read_text()
        self.assertIn(body, (SPIKE / "native/plasma.frag").read_text())
        web = subprocess.run(["node", "--input-type=module", "-e",
                              f"import {{ fragmentSource }} from '{SPIKE}/web/plasma-shader.mjs';"
                              "process.stdout.write(fragmentSource)"],
                             capture_output=True, text=True, check=True).stdout
        self.assertIn(body, web)

    def test_asset_downscale_keeps_alpha_and_size(self):
        from PIL import Image
        img = Image.open(SPIKE / "shared/nexus-plasma-512.png")
        self.assertEqual(img.size, (512, 512))
        self.assertEqual(img.mode, "RGBA")
        alpha = img.getchannel("A")
        self.assertEqual(alpha.getpixel((0, 0)), 0)          # transparent corner
        self.assertGreater(alpha.getpixel((256, 256)), 200)   # opaque heart

    def test_shared_logic_unit_tests(self):
        run = subprocess.run(["node", "--test", str(SPIKE / "shared/suggest.test.mjs")],
                             capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)


if __name__ == "__main__":
    unittest.main()
