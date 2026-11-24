"""nexus-dev db: development databases in containers only this computer can reach."""
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/desktop/nexus-dev.sh"


class DevDatabases(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        (self.tmp / "bin").mkdir()
        self.log = self.tmp / "log"
        fake = self.tmp / "bin/podman"
        fake.write_text(f"""#!/bin/sh
echo "$*" >> {self.log}
if [ "$1 $2" = "container exists" ]; then [ -e {self.tmp}/exists ]; exit $?; fi
exit 0
""")
        fake.chmod(0o755)

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def dev(self, *args):
        env = dict(os.environ, PATH=f"{self.tmp / 'bin'}:{os.environ['PATH']}")
        return subprocess.run(["sh", str(SCRIPT), *args], capture_output=True, text=True, env=env)

    def test_a_database_listens_on_this_computer_only_and_keeps_its_data(self):
        r = self.dev("db", "postgres")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("postgresql://postgres:dev@127.0.0.1:5432/postgres", r.stdout)
        run = [l for l in self.log.read_text().splitlines() if l.startswith("run ")][0]
        self.assertIn("-p 127.0.0.1:5432:5432", run)
        self.assertIn("-v nexus-postgres:/var/lib/postgresql/data", run)
        self.assertNotIn("0.0.0.0", run)
        (self.tmp / "exists").touch()
        self.log.unlink()
        self.assertEqual(self.dev("db", "redis").returncode, 0)
        self.assertIn("start nexus-redis", self.log.read_text())
        self.assertIn("stop nexus-redis", (self.dev("db", "redis", "stop"), self.log.read_text())[1])

    def test_only_known_databases(self):
        for bad in [("db",), ("db", "oracle"), ("db", "postgres", "drop"), ("frobnicate",)]:
            self.assertEqual(self.dev(*bad).returncode, 2, bad)


if __name__ == "__main__":
    unittest.main()
