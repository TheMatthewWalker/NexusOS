import datetime
import os
from pathlib import Path
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "backup"))
import nexus_backup as backup  # noqa: E402

SCRIPT = ROOT / "scripts" / "backup" / "nexus_backup.py"


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dir = Path(self.temp.name)
        self.home = self.dir / "home"
        (self.home / "docs" / "nested").mkdir(parents=True)
        (self.home / "docs" / "note.txt").write_text("important")
        (self.home / "docs" / "nested" / "data.bin").write_bytes(os.urandom(4096))
        (self.home / ".config").mkdir()
        (self.home / ".config" / "app.conf").write_text("x=1")
        os.symlink("docs/note.txt", self.home / "link")
        os.mkfifo(self.home / "pipe")
        self.repo = self.dir / "repo"

    def test_snapshot_restore_round_trip(self):
        name = backup.snapshot(self.home, self.repo)
        self.assertTrue(backup.verify(self.repo, name))
        target = backup.restore(self.repo, name, self.dir / "restored")
        self.assertEqual("important", (target / "docs" / "note.txt").read_text())
        self.assertEqual((self.home / "docs" / "nested" / "data.bin").read_bytes(),
                         (target / "docs" / "nested" / "data.bin").read_bytes())
        self.assertEqual("docs/note.txt", os.readlink(target / "link"))
        self.assertFalse((target / "pipe").exists())  # Special files are not user data.

    def test_repository_and_archives_are_private(self):
        name = backup.snapshot(self.home, self.repo)
        self.assertEqual(0o700, self.repo.stat().st_mode & 0o777)
        self.assertEqual(0o600, (self.repo / (name + backup.SUFFIX)).stat().st_mode & 0o777)
        os.chmod(self.repo, 0o755)
        with self.assertRaises(PermissionError):
            backup.snapshots(self.repo)

    def test_corruption_is_detected_and_blocks_restore(self):
        name = backup.snapshot(self.home, self.repo)
        archive = self.repo / (name + backup.SUFFIX)
        data = bytearray(archive.read_bytes())
        index = data.find(b"important")
        data[index] = ord("I")
        archive.write_bytes(bytes(data))
        self.assertFalse(backup.verify(self.repo, name))
        with self.assertRaises(ValueError):
            backup.restore(self.repo, name, self.dir / "restored")

    def test_restore_never_overwrites(self):
        name = backup.snapshot(self.home, self.repo)
        target = self.dir / "occupied"
        target.mkdir()
        (target / "keep.txt").write_text("mine")
        with self.assertRaises(FileExistsError):
            backup.restore(self.repo, name, target)
        self.assertEqual("mine", (target / "keep.txt").read_text())

    def test_malicious_archive_cannot_escape_target(self):
        backup._repository(self.repo, create=True)
        evil = self.repo / ("20260101T000000Z-evil" + backup.SUFFIX)
        payload = self.dir / "payload"
        payload.write_text("pwned")
        with tarfile.open(evil, "w") as archive:
            archive.add(payload, arcname="data/../../escaped.txt")
            manifest = self.dir / "m.json"
            manifest.write_text('{"version":1,"source":"x","files":[]}')
            archive.add(manifest, arcname=backup.MANIFEST)
        with self.assertRaises(tarfile.TarError):
            backup.restore(self.repo, "20260101T000000Z-evil", self.dir / "restored")
        self.assertFalse((self.dir / "escaped.txt").exists())

    def test_prune_keeps_newest(self):
        base = datetime.datetime(2026, 10, 1, tzinfo=datetime.timezone.utc)
        names = [backup.snapshot(self.home, self.repo, now=base + datetime.timedelta(days=d)) for d in range(4)]
        self.assertEqual(names[-2:], backup.prune(self.repo, 2))
        self.assertEqual(names[-2:], backup.snapshots(self.repo))
        with self.assertRaises(ValueError):
            backup.prune(self.repo, 0)

    def test_invalid_snapshot_names_rejected(self):
        backup._repository(self.repo, create=True)
        for name in ("../x", ".tmp-x", ""):
            with self.assertRaises(ValueError):
                backup.verify(self.repo, name)

    def test_interrupted_backup_leaves_no_partial_and_earlier_snapshots_intact(self):
        first = backup.snapshot(self.home, self.repo)
        big = self.home / "big"
        big.mkdir()
        for index in range(60):
            (big / f"f{index}.bin").write_bytes(os.urandom(1 << 20))
        process = subprocess.Popen([sys.executable, SCRIPT, "snapshot", "--source", self.home, "--repo", self.repo])
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline and not list(self.repo.glob(".tmp-*")):
            time.sleep(0.01)
        self.assertTrue(list(self.repo.glob(".tmp-*")), "backup finished before it could be interrupted")
        process.send_signal(signal.SIGKILL)  # Equivalent to power loss for this process.
        process.wait()
        self.assertEqual([first], backup.snapshots(self.repo))
        self.assertTrue(backup.verify(self.repo, first))
        second = backup.snapshot(self.home, self.repo)
        self.assertEqual([], list(self.repo.glob(".tmp-*")))
        self.assertEqual(sorted([first, second]), backup.snapshots(self.repo))
        self.assertTrue(backup.verify(self.repo, second))

    def test_cli(self):
        out = subprocess.run([sys.executable, SCRIPT, "snapshot", "--source", self.home, "--repo", self.repo, "--keep", "3"],
                             check=True, capture_output=True, text=True).stdout.strip()
        self.assertEqual("ok", subprocess.run([sys.executable, SCRIPT, "verify", "--repo", self.repo, "--snapshot", out],
                                              check=True, capture_output=True, text=True).stdout.strip())
        failed = subprocess.run([sys.executable, SCRIPT, "restore", "--repo", self.repo, "--snapshot", "../etc",
                                 "--target", self.dir / "t"], capture_output=True, text=True)
        self.assertEqual(2, failed.returncode)


if __name__ == "__main__":
    unittest.main()
