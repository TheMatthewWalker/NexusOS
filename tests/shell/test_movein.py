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
                    self.assertEqual(len(r), {"item": 4, "private": 3, "os": 2}[r[0]])

    def test_a_mac_data_volume_is_found(self):
        # The data volume of a Mac (APFS, opened read-only): /Users/<name> next to /Applications and /Library.
        self.make("Users/alice/Documents/thesis.pages")
        self.make("Users/alice/Movies/holiday.mov")
        self.make("Users/alice/Library/Safari/Bookmarks.plist")
        self.make("Users/Shared/Documents/shared.txt")
        self.make("Users/Guest/Documents/guest.txt")
        (self.drive / "Applications/Spotify.app").mkdir(parents=True)
        (self.drive / "Library").mkdir(exist_ok=True)
        (self.drive / "private/var").mkdir(parents=True)
        for shell in SHELLS:
            with self.subTest(shell=shell):
                recs = self.records(shell)
                self.assertIn(["os", "Mac"], recs)
                self.assertEqual(sorted((r[1], r[2]) for r in recs if r[0] == "item"),
                                 [("alice", "Documents"), ("alice", "Movies")])

    def test_a_mac_system_volume_alone_has_nothing_to_copy(self):
        # The sealed system volume: System/Library and an empty Users firmlink folder.
        (self.drive / "System/Library/CoreServices").mkdir(parents=True)
        (self.drive / "Users").mkdir()
        for shell in SHELLS:
            with self.subTest(shell=shell):
                recs = self.records(shell)
                self.assertIn(["os", "Mac"], recs)
                self.assertEqual([r for r in recs if r[0] == "item"], [])

    @unittest.skipUnless(shutil.which("rsync"), "rsync not installed")
    def test_an_apfs_fuse_mount_uses_its_root_folder(self):
        # apfs-fuse shows the volume under root/, next to private-dir/.
        self.make("root/Users/alice/Pictures/cat.heic", "cat")
        (self.drive / "private-dir").mkdir()
        (self.drive / "root/Applications").mkdir()
        for shell in SHELLS:
            with self.subTest(shell=shell):
                recs = self.records(shell)
                self.assertIn(["os", "Mac"], recs)
                self.assertEqual([(r[1], r[2]) for r in recs if r[0] == "item"], [("alice", "Pictures")])
                run = self.run_helper(shell, "copy", str(self.drive), "alice", "Pictures", "0")
                self.assertEqual(run.returncode, 0, run.stderr)
                self.assertEqual((self.home / "Pictures/cat.heic").read_text(), "cat")

    def test_a_symlinked_apfs_fuse_root_is_not_followed(self):
        (self.outside / "Users/eve/Documents").mkdir(parents=True)
        (self.drive / "root").symlink_to(self.outside)
        (self.drive / "private-dir").mkdir()
        for shell in SHELLS:
            with self.subTest(shell=shell):
                self.assertEqual([r for r in self.records(shell) if r[0] == "item"], [])
                run = self.run_helper(shell, "copy", str(self.drive), "eve", "Documents", "0")
                self.assertNotEqual(run.returncode, 0)

    def test_an_old_linux_drive_uses_home(self):
        self.make("etc/os-release", "ID=ubuntu")
        self.make("home/sam/Music/song.flac")
        self.make("home/lost+found/Documents/x")
        self.make("home/.cache/Documents/x")
        for shell in SHELLS:
            with self.subTest(shell=shell):
                recs = self.records(shell)
                self.assertIn(["os", "Linux"], recs)
                self.assertEqual([(r[1], r[2]) for r in recs if r[0] == "item"], [("sam", "Music")])

    def test_users_wins_over_home_in_both_scan_and_copy(self):
        # A crafted drive with both: the same base is used for looking and copying.
        self.make("Users/bob/Documents/a.txt")
        self.make("home/bob/Documents/planted.txt", "planted")
        for shell in SHELLS:
            with self.subTest(shell=shell):
                items = [(r[1], r[2]) for r in self.records(shell) if r[0] == "item"]
                self.assertEqual(items, [("bob", "Documents")])

    def skip_unless_nobody_can_reach(self):
        # As root the tests run the helper as nobody, who must be able to get to the temporary folder.
        if os.geteuid() == 0 and any(not os.stat(p).st_mode & 0o001 for p in self.tmp.parents):
            self.skipTest(f"{self.tmp.parent} is not reachable by an unprivileged account (set TMPDIR)")

    def run_as_nobody(self, shell, *args):
        env = dict(os.environ, HOME=str(self.home))
        return subprocess.run(["setpriv", "--reuid=65534", "--regid=65534", "--clear-groups", shell, str(SCRIPT), *args],
                              capture_output=True, env=env)

    @unittest.skipUnless(os.geteuid() != 0 or shutil.which("setpriv"), "needs an unprivileged account")
    def test_folders_private_to_the_old_account_are_reported_not_copied(self):
        # A Mac or Linux home folder keeps its old owner and mode 700: say so, and do not try to copy it.
        self.skip_unless_nobody_can_reach()
        self.make("Users/alice/Documents/a.txt")
        self.make("Users/alice/Pictures/p.jpg")
        if os.geteuid() == 0:
            # Everything else readable by the unprivileged account, whatever the umask.
            os.chmod(self.tmp, 0o755)
            for path in [self.drive, *self.drive.rglob("*")]:
                os.chmod(path, 0o755 if path.is_dir() else 0o644)
            os.chmod(self.drive / "Users/alice/Documents", 0o700)
            os.chown(self.home, 65534, 65534)
            os.chown(self.drive / "Users/alice/Documents", 0, 0)
            run_helper = self.run_as_nobody
        else:
            os.chmod(self.drive / "Users/alice/Documents", 0)
            run_helper = self.run_helper
        try:
            for shell in SHELLS:
                with self.subTest(shell=shell):
                    run = run_helper(shell, "scan", str(self.drive))
                    self.assertEqual(run.returncode, 0, run.stderr)
                    recs = [r.decode().split("\t") for r in run.stdout.split(b"\0") if r]
                    self.assertIn(["private", "alice", "Documents"], recs)
                    self.assertIn("Pictures", [r[2] for r in recs if r[0] == "item"])
                    run = run_helper(shell, "copy", str(self.drive), "alice", "Documents", "0")
                    self.assertNotEqual(run.returncode, 0)
                    self.assertIn(b"cannot be read", run.stderr)
                    self.assertFalse((self.home / "Documents").exists())
        finally:
            os.chmod(self.drive / "Users/alice/Documents", 0o700)

    @unittest.skipUnless(os.geteuid() != 0 or shutil.which("setpriv"), "needs an unprivileged account")
    def test_an_unreadable_volume_is_reported(self):
        # An encrypted Mac volume that mounts but cannot be listed.
        self.skip_unless_nobody_can_reach()
        os.chmod(self.tmp, 0o755)
        os.chmod(self.drive, 0)
        try:
            for shell in SHELLS:
                with self.subTest(shell=shell):
                    run = (self.run_as_nobody if os.geteuid() == 0 else self.run_helper)(shell, "scan", str(self.drive))
                    self.assertEqual(run.returncode, 0, run.stderr)
                    self.assertEqual(run.stdout, b"unreadable\0")
        finally:
            os.chmod(self.drive, 0o755)

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
