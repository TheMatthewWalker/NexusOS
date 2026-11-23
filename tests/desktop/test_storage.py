"""Settings, Storage: sizes are reported, and emptying the trash or caches removes only what it should."""
import importlib.util
import os
from pathlib import Path
import shutil
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_storage", ROOT / "scripts/desktop/nexus_storage.py")
S = importlib.util.module_from_spec(spec)
spec.loader.exec_module(S)


class Storage(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.home = self.tmp / "home"
        (self.home / "Documents").mkdir(parents=True)
        (self.home / "Documents" / "big").write_bytes(b"x" * 200_000)
        self.saved = (S.HOME, S.TRASH, S.CACHE, S.FOLDERS)
        S.HOME, S.TRASH, S.CACHE = self.home, self.home / ".local/share/Trash", self.home / ".cache"
        S.FOLDERS = [("Documents", self.home / "Documents"), ("Trash", S.TRASH), ("Caches", S.CACHE)]
        (S.TRASH / "files" / "old folder").mkdir(parents=True)
        (S.TRASH / "files" / "old folder" / "a").write_bytes(b"y" * 50_000)
        (S.TRASH / "info").mkdir()
        (S.TRASH / "info" / "old folder.trashinfo").write_text("[Trash Info]\n")
        (S.CACHE / "mozilla").mkdir(parents=True)
        (S.CACHE / "mozilla" / "c").write_bytes(b"z" * 10_000)
        (S.CACHE / "nexusos").mkdir()
        (S.CACHE / "nexusos" / "model.part").write_bytes(b"m" * 10)

    def tearDown(self):
        S.HOME, S.TRASH, S.CACHE, S.FOLDERS = self.saved
        shutil.rmtree(self.tmp)

    def test_report_has_the_disk_and_each_kind(self):
        r = S.report()
        self.assertGreaterEqual(len(r["volumes"]), 1)
        kinds = {k["label"]: k["bytes"] for k in r["kinds"]}
        self.assertGreaterEqual(kinds["Documents"], 200_000)
        self.assertIn("Trash", kinds)

    def test_emptying_the_trash_and_caches(self):
        self.assertGreater(S.empty_trash()["freedBytes"], 0)
        self.assertEqual(list((S.TRASH / "files").iterdir()), [])
        self.assertEqual(list((S.TRASH / "info").iterdir()), [])
        S.clear_cache()
        self.assertFalse((S.CACHE / "mozilla").exists())
        self.assertTrue((S.CACHE / "nexusos" / "model.part").exists())
        self.assertTrue((self.home / "Documents" / "big").exists())

    def test_old_system_versions_go_but_the_last_three_stay(self):
        profiles = self.tmp / "profiles"
        profiles.mkdir()
        old = time.time() - 30 * 86400
        (profiles / "system").symlink_to("system-2-link")       # went back to version 2
        for n in range(1, 12):
            link = profiles / f"system-{n}-link"
            link.symlink_to("/nowhere")
            os.utime(link, (old, old) if n < 11 else None, follow_symlinks=False)
        S.PROFILES, calls = profiles, []
        euid = os.geteuid
        os.geteuid = lambda: 0
        try:
            r = S.clean_system(lambda c, timeout=None: (calls.append(c), (0, "", ""))[1])
        finally:
            os.geteuid = euid
        deleted = [c[-1] for c in calls if c[0] == "nix-env"]
        self.assertEqual(deleted, ["1", "3", "4", "5", "6", "7", "8"])   # 2 (in use), 9, 10 and 11 (newest) stay
        self.assertIn(["nix-store", "--gc"], calls)
        self.assertNotIn("error", r)


if __name__ == "__main__":
    unittest.main()
