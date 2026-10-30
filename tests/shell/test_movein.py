"""Move in's helper (shell/qml/lib/movein.sh) against a crafted old drive.

The drive is not trusted: whatever its folder names, symlinks and special
files, nothing may land outside ~/Documents, Pictures, Music, Videos,
Downloads or Desktop. Runs with every POSIX shell installed (dash, bash).

    python3 -m unittest tests/shell/test_movein.py
"""
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "shell/qml/lib/movein.sh"
ALLOWED = {"Documents", "Pictures", "Music", "Videos", "Downloads", "Desktop"}
SHELLS = [s for s in ("dash", "bash", "sh") if shutil.which(s)]


class MoveInHelper(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="nexus-movein-"))
        self.drive = self.tmp / "drive"
        self.home = self.tmp / "home"
        self.outside = self.tmp / "outside"
        for d in (self.drive, self.home, self.outside):
            d.mkdir()
        (self.outside / "Documents").mkdir()
        (self.outside / "Documents/secret").write_text("not from the drive")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_helper(self, shell, *args):
        env = dict(os.environ, HOME=str(self.home))
        return subprocess.run([shell, str(SCRIPT), *args], capture_output=True, env=env)

    def make(self, rel, text="x"):
        path = self.drive / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        return path

    def assert_home_only_has_allowed_folders(self):
        for path in self.home.rglob("*"):
            top = path.relative_to(self.home).parts[0]
            self.assertIn(top, ALLOWED, f"{path} is outside the allow-list")
            self.assertFalse(path.is_symlink(), f"{path} is a symlink")
            if path.is_file():
                self.assertNotEqual(path.read_text(), "not from the drive")

    def records(self, shell):
        run = self.run_helper(shell, "scan", str(self.drive))
        self.assertEqual(run.returncode, 0, run.stderr)
        self.assertNotIn(b"\n", run.stdout)
        return [r.decode().split("\t") for r in run.stdout.split(b"\0") if r]

    def test_scan_lists_only_plain_folders_with_safe_names(self):
        self.make("Users/bob/Documents/a.txt")
        self.make("Users/bob/Movies/m.mp4")
        self.make("Users/a\nb/Documents/x")
        self.make("Users/tab\there/Documents/x")
        self.make("Users/.hidden/Documents/x")
        self.make("Users/Public/Documents/x")
        self.make("Users/carol/.ssh/authorized_keys")
        self.make("Pictures/p.jpg")
        (self.drive / "Users/evil").symlink_to(self.outside)
        (self.drive / "Users/bob/Pictures").symlink_to(self.outside / "Documents")
        (self.drive / "Music").symlink_to(self.outside)
        (self.drive / "ProgramData").mkdir()
        for shell in SHELLS:
            with self.subTest(shell=shell):
                recs = self.records(shell)
                self.assertIn(["os", "Windows"], recs)
                items = sorted((r[1], r[2]) for r in recs if r[0] == "item")
                self.assertEqual(items, [("", "Pictures"), ("bob", "Documents"), ("bob", "Movies")])
                for r in recs:
                    self.assertEqual(len(r), 4 if r[0] == "item" else 2)

    def test_scan_skips_a_symlinked_users_folder(self):
        (self.outside / "bob/Documents").mkdir(parents=True)
        (self.drive / "Users").symlink_to(self.outside)
        for shell in SHELLS:
            with self.subTest(shell=shell):
                self.assertEqual([r for r in self.records(shell) if r[0] == "item"], [])

    def test_crafted_names_are_refused(self):
        self.make("Users/bob/Documents/a.txt")
        self.make(".bashrc", "planted")
        self.make("Users/bob/.ssh/authorized_keys", "planted")
        crafted = [
            ("../.bashrc", "Documents"), ("..", "Documents"), (".", "Documents"), ("../..", "Documents"),
            ("a\nb", "Documents"), ("/etc", "Documents"), ("bob/../..", "Documents"), (".ssh", "Documents"),
            ("bob", ".ssh"), ("bob", "../.bashrc"), ("bob", "/etc"), ("", ".config/autostart"), ("", ".."),
            ("bob", "Documents\n"), ("bob", "AppData"),
        ]
        for shell in SHELLS:
            for user, folder in crafted:
                for per_user in ("0", "1"):
                    with self.subTest(shell=shell, user=user, folder=folder, per_user=per_user):
                        run = self.run_helper(shell, "copy", str(self.drive), user, folder, per_user)
                        self.assertNotEqual(run.returncode, 0)
                        self.assertEqual(list(self.home.iterdir()), [])

    def test_relative_or_control_character_roots_are_refused(self):
        for shell in SHELLS:
            for root in ["drive", "../drive", str(self.drive) + "\n"]:
                with self.subTest(shell=shell, root=root):
                    run = self.run_helper(shell, "copy", root, "", "Documents", "0")
                    self.assertNotEqual(run.returncode, 0)

    @unittest.skipUnless(shutil.which("rsync"), "rsync not installed")
    def test_copy_lands_only_in_the_allow_list(self):
        self.make("Users/bob/Documents/report.txt", "mine")
        self.make("Users/bob/Documents/.bashrc", "dotfile inside Documents stays there")
        self.make("Users/bob/Documents/sub/deep.txt", "deep")
        (self.drive / "Users/bob/Documents/passwd").symlink_to("/etc/passwd")
        (self.drive / "Users/bob/Documents/up").symlink_to("../../..")
        (self.drive / "Users/bob/Documents/away").symlink_to(self.outside)
        os.mkfifo(self.drive / "Users/bob/Documents/fifo")
        os.chmod(self.drive / "Users/bob/Documents/report.txt", 0o4755)
        self.make("Movies/film.mkv", "film")
        for shell in SHELLS:
            with self.subTest(shell=shell):
                run = self.run_helper(shell, "copy", str(self.drive), "bob", "Documents", "1")
                self.assertEqual(run.returncode, 0, run.stderr)
                run = self.run_helper(shell, "copy", str(self.drive), "", "Movies", "0")
                self.assertEqual(run.returncode, 0, run.stderr)
                docs = self.home / "Documents/bob"
                self.assertEqual((docs / "report.txt").read_text(), "mine")
                self.assertEqual((docs / "sub/deep.txt").read_text(), "deep")
                self.assertEqual((self.home / "Videos/film.mkv").read_text(), "film")
                self.assertEqual(os.stat(docs / "report.txt").st_mode & 0o7000, 0)
                for name in ["passwd", "up", "away", "fifo"]:
                    self.assertFalse(os.path.lexists(docs / name), name)
                self.assertEqual(sorted(p.name for p in self.home.iterdir()), ["Documents", "Videos"])
                self.assert_home_only_has_allowed_folders()

    @unittest.skipUnless(shutil.which("rsync"), "rsync not installed")
    def test_existing_files_are_kept(self):
        self.make("Pictures/p.jpg", "from the drive")
        (self.home / "Pictures").mkdir()
        (self.home / "Pictures/p.jpg").write_text("already here")
        for shell in SHELLS:
            with self.subTest(shell=shell):
                run = self.run_helper(shell, "copy", str(self.drive), "", "Pictures", "0")
                self.assertEqual(run.returncode, 0, run.stderr)
                self.assertEqual((self.home / "Pictures/p.jpg").read_text(), "already here")

    def test_symlinked_destinations_are_refused(self):
        self.make("Users/bob/Documents/authorized_keys", "planted")
        ssh = self.home / ".ssh"
        ssh.mkdir()
        (self.home / "Documents").symlink_to(ssh)
        for shell in SHELLS:
            with self.subTest(shell=shell):
                run = self.run_helper(shell, "copy", str(self.drive), "bob", "Documents", "0")
                self.assertNotEqual(run.returncode, 0)
                self.assertIn(b"symlink", run.stderr)
                self.assertEqual(list(ssh.iterdir()), [])
        (self.home / "Documents").unlink()
        (self.home / "Documents").mkdir()
        (self.home / "Documents/bob").symlink_to(ssh)
        for shell in SHELLS:
            with self.subTest(shell=shell, per_user=True):
                run = self.run_helper(shell, "copy", str(self.drive), "bob", "Documents", "1")
                self.assertNotEqual(run.returncode, 0)
                self.assertEqual(list(ssh.iterdir()), [])

    def test_symlinked_sources_are_refused(self):
        (self.drive / "Users/bob").mkdir(parents=True)
        (self.drive / "Users/bob/Documents").symlink_to(self.outside / "Documents")
        (self.drive / "Users/eve").symlink_to(self.outside)
        (self.drive / "Pictures").symlink_to(self.outside / "Documents")
        for shell in SHELLS:
            for user, folder in [("bob", "Documents"), ("eve", "Documents"), ("", "Pictures")]:
                with self.subTest(shell=shell, user=user, folder=folder):
                    run = self.run_helper(shell, "copy", str(self.drive), user, folder, "0")
                    self.assertNotEqual(run.returncode, 0)
                    self.assertFalse((self.home / folder / "secret").exists())


if __name__ == "__main__":
    unittest.main()
