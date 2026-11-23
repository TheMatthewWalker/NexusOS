"""Shared folders: only folders in the home folder, read-only, never hidden ones."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_share", ROOT / "scripts/desktop/nexus_share.py")
S = importlib.util.module_from_spec(spec)
spec.loader.exec_module(S)

INFO = "[Photos]\npath=/home/x/Photos\ncomment=Shared from NexusOS\nusershare_acl=Everyone:R,\nguest_ok=y\n"


class Fake:
    def __init__(self, info=""):
        self.calls, self.info = [], info

    def __call__(self, cmd, **kw):
        self.calls.append(cmd)
        return subprocess.CompletedProcess(cmd, 0, self.info if cmd[:3] == ["net", "usershare", "info"] else "", "")


class SharedFolders(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp()).resolve()
        (self.tmp / "Music").mkdir()
        (self.tmp / ".ssh").mkdir()
        (self.tmp / "Music" / "song.txt").write_text("x")
        self.env = mock.patch.dict(os.environ, {"HOME": str(self.tmp)})
        self.env.start()

    def tearDown(self):
        self.env.stop()
        shutil.rmtree(self.tmp)

    def test_a_home_folder_is_shared_read_only(self):
        f = Fake()
        r = S.add(str(self.tmp / "Music"), f)
        self.assertEqual(r["name"], "Music")
        self.assertIn(["net", "usershare", "add", "Music", str(self.tmp / "Music"), "Shared from NexusOS", "Everyone:R", "guest_ok=y"], f.calls)

    def test_home_itself_hidden_folders_files_and_outside_are_refused(self):
        for bad in [str(self.tmp), str(self.tmp / ".ssh"), str(self.tmp / "Music" / "song.txt"), "/etc", "/nonexistent", str(self.tmp / "Music/../.ssh")]:
            self.assertIn("error", S.add(bad, Fake()), bad)

    def test_list_and_remove(self):
        f = Fake(INFO)
        self.assertEqual(S.listing(f), [{"name": "Photos", "path": "/home/x/Photos"}])
        self.assertIn("no longer shared", S.remove("Photos", f)["message"])
        self.assertIn(["net", "usershare", "delete", "Photos"], f.calls)
        self.assertIn("error", S.remove("Nope", Fake(INFO)))
        self.assertIn("error", S.remove("../x", Fake(INFO)))
        self.assertIn("already exists", S.add(str(self.tmp / "Music"), Fake("[music]\npath=/x\n"))["error"])


if __name__ == "__main__":
    unittest.main()
