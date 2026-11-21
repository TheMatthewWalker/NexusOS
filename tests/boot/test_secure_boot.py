"""Secure Boot: the next step is read from the firmware, and keys are only enrolled in Setup Mode."""
import importlib.util
import os
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_secure_boot", ROOT / "scripts/boot/nexus_secure_boot.py")
B = importlib.util.module_from_spec(spec)
spec.loader.exec_module(B)


class Done:
    returncode, stdout, stderr = 0, "", ""


class SecureBoot(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.saved = (B.EFIVARS, B.KEYS, B.STATE)
        B.EFIVARS, B.KEYS, B.STATE = self.tmp / "efivars", self.tmp / "keys", self.tmp / "state"
        B.EFIVARS.mkdir()
        self.euid = os.geteuid

    def tearDown(self):
        B.EFIVARS, B.KEYS, B.STATE = self.saved
        os.geteuid = self.euid
        shutil.rmtree(self.tmp)

    def firmware(self, secure_boot, setup_mode):
        for name, value in (("SecureBoot", secure_boot), ("SetupMode", setup_mode)):
            (B.EFIVARS / f"{name}-{B.GLOBAL}").write_bytes(b"\x06\x00\x00\x00" + bytes([value]))

    def test_the_steps_in_order(self):
        self.firmware(0, 0)
        self.assertEqual(B.status(False)["step"], "prepare")
        self.assertEqual(B.status(True)["step"], "setup")
        self.firmware(0, 1)
        self.assertEqual(B.status(True)["step"], "enroll")
        self.firmware(0, 0)
        B.STATE.mkdir()
        (B.STATE / "enrolled").write_text("1\n")
        self.assertEqual(B.status(True)["step"], "turn-on")
        self.firmware(1, 0)
        self.assertEqual(B.status(True)["step"], "on")
        self.assertEqual(B.status(False)["step"], "on")
        shutil.rmtree(B.EFIVARS)
        self.assertEqual(B.status(True)["step"], "unsupported")

    def test_enrolling_only_in_setup_mode_with_keys(self):
        calls = []
        runner = lambda c, **k: (calls.append(c), Done())[1]
        os.geteuid = lambda: 1000
        self.assertIn("administrator", B.enroll(True, runner)["error"])
        os.geteuid = lambda: 0
        self.firmware(0, 1)
        self.assertIn("not prepared", B.enroll(True, runner)["error"])     # no keys yet
        B.KEYS.mkdir()
        self.assertIn("not prepared", B.enroll(False, runner)["error"])
        self.firmware(0, 0)
        self.assertIn("Setup Mode", B.enroll(True, runner)["error"])
        self.assertEqual(calls, [])
        self.firmware(0, 1)
        self.assertIn("message", B.enroll(True, runner))
        self.assertEqual(calls, [[B.SBCTL, "enroll-keys", "--microsoft", "--firmware-builtin"]])
        self.assertTrue((B.STATE / "enrolled").exists())


if __name__ == "__main__":
    unittest.main()
