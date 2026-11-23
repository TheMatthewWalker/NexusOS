"""Settings, Accounts, Cloud drives: rclone remotes become folders, and nothing secret goes on a command line."""
import configparser
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_cloud", ROOT / "scripts/desktop/nexus_cloud.py")
C = importlib.util.module_from_spec(spec)
spec.loader.exec_module(C)


class Fake:
    """Stands in for rclone and systemctl; `config create` writes a section as rclone would."""
    def __init__(self, ok=True):
        self.calls, self.ok = [], ok

    def __call__(self, cmd, **kw):
        self.calls.append((cmd, kw))
        code, out = 0, ""
        if cmd[:3] == ["rclone", "config", "create"] and self.ok:
            cp = C.read_config()
            cp[cmd[3]] = dict([("type", cmd[4])] + [tuple(a.split("=", 1)) for a in cmd[5:]] + [("token", "{}")])
            C.config_path().parent.mkdir(parents=True, exist_ok=True)
            with open(C.config_path(), "w") as f:
                cp.write(f)
        elif cmd[:3] == ["rclone", "config", "create"]:
            code = 1
        elif cmd[:2] == ["rclone", "obscure"]:
            out = "OBSCURED\n"
        elif cmd[:2] == ["rclone", "lsd"]:
            code = 0 if self.ok else 1
        elif cmd[:3] == ["rclone", "config", "delete"] and C.config_path().is_file():
            cp = C.read_config()
            cp.remove_section(cmd[3])
            with open(C.config_path(), "w") as f:
                cp.write(f)
        return subprocess.CompletedProcess(cmd, code, out, "")


class CloudDrives(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.env = mock.patch.dict(os.environ, {"HOME": str(self.tmp), "XDG_CONFIG_HOME": str(self.tmp / ".config")})
        self.env.start()

    def tearDown(self):
        self.env.stop()
        shutil.rmtree(self.tmp)

    def test_onedrive_signs_in_then_becomes_a_folder_in_files(self):
        fake = Fake()
        r = C.add("onedrive", "OneDrive", fake)
        self.assertEqual(r, {"message": "OneDrive is in your home folder and in Files."})
        self.assertIn((["rclone", "config", "create", "OneDrive", "onedrive", "nexus_kind=onedrive"], {}), fake.calls)
        self.assertIn(["systemctl", "--user", "start", "nexus-cloud@OneDrive.service"], [c for c, _ in fake.calls])
        self.assertTrue((self.tmp / "OneDrive").is_dir())
        self.assertIn((self.tmp / "OneDrive").as_uri() + " OneDrive", C.bookmarks_path().read_text())
        [d] = C.listing()
        self.assertEqual((d["name"], d["kind"], d["label"], d["mounted"]), ("OneDrive", "onedrive", "OneDrive", False))

    def test_a_sign_in_that_does_not_finish_leaves_nothing_behind(self):
        fake = Fake(ok=False)
        self.assertIn("error", C.add("dropbox", "Dropbox", fake))
        self.assertEqual(C.listing(), [])
        self.assertFalse(any(c[:2] == ["systemctl", "--user"] for c, _ in fake.calls))

    def test_names_and_existing_folders_are_checked(self):
        for bad in ["", "../x", "1drive", "a b", "x" * 31, "-rf"]:
            self.assertIn("error", C.add("onedrive", bad, Fake()), bad)
        (self.tmp / "Dropbox").mkdir()
        (self.tmp / "Dropbox" / "old.txt").write_text("mine")
        self.assertIn("already a Dropbox folder", C.add("dropbox", "Dropbox", Fake())["error"])
        self.assertIn("error", C.add("icloud", "Cloud", Fake()))

    def test_nextcloud_password_goes_on_stdin_and_the_file_is_private(self):
        fake = Fake()
        r = C.add_nextcloud("Work", "https://cloud.example.com/", "sam", "app-pass-123", fake)
        self.assertNotIn("error", r)
        for cmd, kw in fake.calls:
            self.assertNotIn("app-pass-123", " ".join(cmd))
        self.assertIn((["rclone", "obscure", "-"], {"input": "app-pass-123"}), fake.calls)
        cp = configparser.ConfigParser(interpolation=None)
        cp.read(C.config_path())
        self.assertEqual(cp["Work"]["url"], "https://cloud.example.com/remote.php/dav/files/sam")
        self.assertEqual(cp["Work"]["pass"], "OBSCURED")
        self.assertEqual(oct(C.config_path().stat().st_mode & 0o777), "0o600")

    def test_nextcloud_that_refuses_is_forgotten(self):
        self.assertIn("error", C.add_nextcloud("Work", "https://cloud.example.com", "sam", "wrong", Fake(ok=False)))
        self.assertEqual(C.listing(), [])
        for url in ["http://cloud.example.com", "https://x.com/a b", "file:///etc"]:
            self.assertIn("error", C.add_nextcloud("Work", url, "sam", "p", Fake()), url)

    def test_remove_stops_forgets_and_tidies(self):
        fake = Fake()
        C.add("googledrive", "GoogleDrive", fake)
        r = C.remove("GoogleDrive", fake)
        self.assertIn("still in the cloud", r["message"])
        self.assertIn(["systemctl", "--user", "stop", "nexus-cloud@GoogleDrive.service"], [c for c, _ in fake.calls])
        self.assertEqual(C.listing(), [])
        self.assertFalse((self.tmp / "GoogleDrive").exists())
        self.assertNotIn("GoogleDrive", C.bookmarks_path().read_text())
        self.assertIn("error", C.remove("Nope", fake))

    def test_start_all_mounts_each_drive_at_login(self):
        fake = Fake()
        C.add("onedrive", "OneDrive", fake)
        C.add("dropbox", "Dropbox", fake)
        fake.calls.clear()
        C.start_all(fake)
        self.assertEqual(sorted(c[-1] for c, _ in fake.calls), ["nexus-cloud@Dropbox.service", "nexus-cloud@OneDrive.service"])


if __name__ == "__main__":
    unittest.main()
