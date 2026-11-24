"""nexus-scan: on-demand virus scans that say what they found and never delete."""
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/desktop/nexus-scan.sh"


class Scan(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        (self.tmp / "bin").mkdir()
        (self.tmp / "db").mkdir()
        (self.tmp / "home/Downloads").mkdir(parents=True)
        self.bad = self.tmp / "home/Downloads/setup.exe"
        self.bad.write_text("x")
        for tool, body in {"notify-send": "exit 0",
                           "clamscan": f'for a; do t="$a"; done; [ -e "{self.tmp}/infected" ] || exit 0; '
                                       f'echo "$t/setup.exe: Win.Trojan.Agent-123 FOUND"; exit 1'}.items():
            f = self.tmp / "bin" / tool
            f.write_text("#!/bin/sh\n" + body + "\n")
            f.chmod(0o755)

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def scan(self, *args):
        env = dict(os.environ, PATH=f"{self.tmp / 'bin'}:{os.environ['PATH']}", HOME=str(self.tmp / "home"),
                   NEXUS_CLAMAV_DB=str(self.tmp / "db"))
        return subprocess.run(["bash", str(SCRIPT), *args], capture_output=True, text=True, env=env)

    def test_waits_for_definitions_then_scans(self):
        self.assertEqual(self.scan().returncode, 3)
        (self.tmp / "db/main.cvd").write_text("")
        r = self.scan()
        self.assertEqual((r.returncode, r.stdout.strip()), (0, "No viruses found in Downloads."))
        (self.tmp / "infected").touch()
        r = self.scan()
        self.assertEqual(r.returncode, 1)
        self.assertIn("Found 1 infected file(s) in Downloads: setup.exe. Nothing was deleted", r.stdout)
        self.assertTrue(self.bad.exists())
        self.assertEqual(self.scan(str(self.tmp / "nowhere")).returncode, 2)


if __name__ == "__main__":
    unittest.main()
