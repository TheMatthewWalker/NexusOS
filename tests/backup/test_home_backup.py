"""Settings, Backups: nexus-backup sets a drive up, backs up, keeps state, and restores without touching home."""
import glob
import importlib.util
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_home_backup", ROOT / "scripts/backup/nexus_home_backup.py")
B = importlib.util.module_from_spec(spec)
spec.loader.exec_module(B)

RESTIC = shutil.which("restic") or next(iter(sorted(glob.glob("/nix/store/*-restic-*/bin/restic"))), None)


class Home(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.home = self.tmp / "home" / "ada"
        (self.home / "Documents").mkdir(parents=True)
        (self.home / "Documents" / "budget.txt").write_text("April: 120\n")
        (self.home / ".cache").mkdir()
        (self.home / ".cache" / "junk").write_text("x" * 100)
        self.media = self.tmp / "media"
        (self.media / "USB DRIVE").mkdir(parents=True)
        config = self.home / ".config" / "nexusos"
        self.saved = {k: getattr(B, k) for k in ("HOME", "SETTINGS", "PASSWORD", "STATE", "MEDIA", "RESTIC")}
        B.HOME, B.MEDIA = self.home, self.media
        B.SETTINGS, B.PASSWORD, B.STATE = config / "backup.json", config / "backup-password", config / "backup-state.json"
        if RESTIC:
            B.RESTIC = RESTIC

    def tearDown(self):
        for k, v in self.saved.items():
            setattr(B, k, v)
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_drives_are_listed_with_free_space(self):
        drives = B.drives(self.media)
        self.assertEqual([d["name"] for d in drives], ["USB DRIVE"])
        self.assertGreater(drives[0]["totalBytes"], 0)
        self.assertFalse(drives[0]["hasBackup"])
        self.assertEqual(B.drives(self.tmp / "nowhere"), [])

    def test_setup_refuses_a_short_password_or_a_missing_drive(self):
        with self.assertRaisesRegex(B.BackupError, "at least"):
            B.setup(self.media / "USB DRIVE", "short")
        with self.assertRaisesRegex(B.BackupError, "not plugged in"):
            B.setup(self.media / "Gone", "a long enough password")
        self.assertFalse(B.PASSWORD.exists())

    def test_a_failed_init_leaves_nothing_behind(self):
        def runner(command, env=None):
            return 1, "", "Fatal: create repository failed: permission denied\n"
        with self.assertRaisesRegex(B.BackupError, "permission denied"):
            B.setup(self.media / "USB DRIVE", "a long enough password", runner)
        self.assertFalse(B.PASSWORD.exists())
        self.assertFalse(B.SETTINGS.exists())

    def test_the_automatic_backup_runs_once_a_day_when_the_drive_is_there(self):
        day = 24 * 3600
        self.assertTrue(B.due({}, True, True, 10 * day))
        self.assertFalse(B.due({"lastBackup": 10 * day}, True, True, 10 * day + 3600))
        self.assertTrue(B.due({"lastBackup": 10 * day}, True, True, 11 * day))
        self.assertFalse(B.due({}, True, False, 10 * day))          # drive not plugged in
        self.assertFalse(B.due({}, False, True, 10 * day))          # not set up
        self.assertFalse(B.due({"state": "running", "startedAt": 10 * day}, True, True, 10 * day + 60))
        self.assertTrue(B.due({"state": "running", "startedAt": 10 * day}, True, True, 11 * day))  # stale

    def test_restore_takes_only_a_backup_id_and_a_folder_in_home(self):
        B.save(B.SETTINGS, {"dest": str(self.media / "USB DRIVE"), "repository": "r"})
        B.save(B.PASSWORD, "a long enough password")
        with self.assertRaisesRegex(B.BackupError, "Not a backup"):
            B.restore("--target=/etc", runner=lambda c, e=None: (0, "", ""))
        with self.assertRaisesRegex(B.BackupError, "home folder"):
            B.restore("abcdef12", "/etc/shadow", runner=lambda c, e=None: (0, "", ""))
        with self.assertRaisesRegex(B.BackupError, "home folder"):
            B.restore("abcdef12", str(self.home) + "2/x", runner=lambda c, e=None: (0, "", ""))
        with self.assertRaisesRegex(B.BackupError, "home folder"):
            B.restore("abcdef12", str(self.home / ".." / "bob"), runner=lambda c, e=None: (0, "", ""))
        self.assertFalse((self.home / "Restored files").exists())

    def test_a_failed_backup_is_reported_in_the_state(self):
        B.save(B.SETTINGS, {"dest": str(self.media / "USB DRIVE"), "repository": "r"})
        B.save(B.PASSWORD, "a long enough password")
        with self.assertRaisesRegex(B.BackupError, "stopped"):
            B.backup(lambda c, e=None: (1, "", "Fatal: unable to open repository\n"))
        self.assertEqual(B.status()["state"], "failed")
        self.assertIn("unable to open", B.status()["message"])

    def test_a_drive_plugged_in_but_not_opened_is_opened_for_the_automatic_backup(self):
        drive = self.media / "USB DRIVE"
        calls = []
        def runner(command, env=None):
            calls.append(command[0])
            if command[0] == "udisksctl":
                return 0, f"Mounted /dev/sdb1 at {drive}.\n", ""
            return 0, "", ""
        s = B.mount_drive({"dest": "/run/media/ada/OLD", "repository": "x", "uuid": "1234-ABCD"}, runner)
        self.assertEqual(s["dest"], str(drive))
        self.assertEqual(B.load(B.SETTINGS, {})["repository"], str(drive / "NexusOS backup"))
        self.assertEqual(B.mount_drive({"dest": "x", "repository": "x"}, runner)["dest"], "x")   # no id: nothing to open
        self.assertEqual(calls, ["udisksctl"])
        self.assertIsNone(B.drive_uuid(drive, lambda c, e=None: (0, "--evil\n", "")))

    @unittest.skipUnless(RESTIC, "restic is not installed")
    def test_back_up_and_restore_with_restic(self):
        os.environ["RESTIC_CACHE_DIR"] = str(self.tmp / "cache")
        drive = self.media / "USB DRIVE"
        B.setup(drive, "a long enough password")
        self.assertTrue(B.drives(self.media)[0]["hasBackup"])
        self.assertEqual(oct(B.PASSWORD.stat().st_mode & 0o777), "0o600")
        result = B.backup()
        self.assertEqual(result["state"], "done")
        status = B.status()
        self.assertTrue(status["setUp"] and status["drivePresent"])
        self.assertIsNotNone(status["lastBackup"])
        snaps = B.snapshots()
        self.assertEqual(len(snaps), 1)

        (self.home / "Documents" / "budget.txt").write_text("changed\n")
        restored = B.restore(snaps[0]["id"], str(self.home / "Documents"))
        folder = Path(restored["folder"])
        self.assertEqual((folder / "Documents" / "budget.txt").read_text(), "April: 120\n")
        self.assertFalse((folder / ".cache").exists())         # caches are never backed up
        self.assertEqual((self.home / "Documents" / "budget.txt").read_text(), "changed\n")  # home untouched

        # Set up again on the same drive: the right password opens it, a wrong one is refused.
        B.setup(drive, "a long enough password")
        with self.assertRaisesRegex(B.BackupError, "does not open it"):
            B.setup(drive, "a different password!")


if __name__ == "__main__":
    unittest.main()
