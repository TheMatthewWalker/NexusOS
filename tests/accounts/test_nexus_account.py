"""Settings, Accounts: the root helper's password change keeps the disk's password in step, safely."""
import importlib.util
import os
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_account", ROOT / "scripts/accounts/nexus_account.py")
A = importlib.util.module_from_spec(spec)
spec.loader.exec_module(A)


class FakeMachine:
    """cryptsetup and chpasswd as the helper calls them: a disk with key slots, and login passwords."""
    def __init__(self, disk_keys=("old-password-1",), encrypted=True, chpasswd_fails=False):
        self.keys = list(disk_keys)
        self.encrypted = encrypted
        self.chpasswd_fails = chpasswd_fails
        self.logins = {}
        self.commands = []

    def run(self, command, input_text=None):
        self.commands.append(command[0] + " " + (command[1] if len(command) > 1 else ""))
        if command[:2] == ["cryptsetup", "status"]:
            return (0, "  type:    LUKS2\n  device:  /dev/nvme0n1p2\n", "") if self.encrypted else (4, "", "inactive")
        if command[0] == "cryptsetup":
            key = Path(command[command.index("--key-file") + 1]).read_text()
            if command[1] == "open":
                return (0 if key in self.keys else 2), "", ""
            if command[1] == "luksAddKey":
                if key not in self.keys:
                    return 2, "", "No key available"
                self.keys.append(Path(command[-1]).read_text())
                return 0, "", ""
            if command[1] == "luksRemoveKey":
                if key not in self.keys:
                    return 2, "", "No key"
                self.keys.remove(key)
                return 0, "", ""
        if command[0] == "chpasswd":
            if self.chpasswd_fails:
                return 1, "", "chpasswd: PAM: Authentication token manipulation error"
            login, _, password = input_text.rstrip("\n").partition(":")
            self.logins[login] = password
            return 0, "", ""
        return 0, "", ""


class PasswordChange(unittest.TestCase):
    def test_the_disk_follows_the_login_password(self):
        m = FakeMachine()
        result = A.change_password("ada", "old-password-1", "new-password-2", m.run)
        self.assertEqual("changed", result["disk"])
        self.assertEqual(["new-password-2"], m.keys)               # the old password no longer opens the disk
        self.assertEqual("new-password-2", m.logins["ada"])
        # The new password is on the disk before the login one changes, the old one leaves last.
        order = [c for c in m.commands if c.split()[0] in ("chpasswd",) or "luks" in c]
        self.assertEqual(["cryptsetup luksAddKey", "chpasswd ", "cryptsetup luksRemoveKey"], order)

    def test_a_failed_login_change_leaves_the_disk_as_it_was(self):
        m = FakeMachine(chpasswd_fails=True)
        with self.assertRaisesRegex(A.AccountError, "could not be changed"):
            A.change_password("ada", "old-password-1", "new-password-2", m.run)
        self.assertEqual(["old-password-1"], m.keys)

    def test_a_disk_with_a_different_password_is_left_alone_and_said_so(self):
        m = FakeMachine(disk_keys=("disk-only-pass",))
        result = A.change_password("ada", "old-password-1", "new-password-2", m.run)
        self.assertEqual("different", result["disk"])
        self.assertEqual(["disk-only-pass"], m.keys)
        self.assertIn("enter the disk's password", result["message"])
        # Then the disk's own password brings it in step.
        self.assertEqual("changed", A.change_disk_password("disk-only-pass", "new-password-2", m.run)["disk"])
        self.assertEqual(["new-password-2"], m.keys)
        with self.assertRaisesRegex(A.AccountError, "not the disk's password"):
            A.change_disk_password("wrong-password", "another-pass", m.run)

    def test_an_unencrypted_computer_changes_only_the_login(self):
        m = FakeMachine(encrypted=False)
        self.assertEqual("none", A.change_password("ada", "old-password-1", "new-password-2", m.run)["disk"])

    def test_short_or_same_passwords_are_refused(self):
        m = FakeMachine()
        for new in ("short", "old-password-1", "has\nnewline-in"):
            with self.subTest(new=new), self.assertRaises(A.AccountError):
                A.change_password("ada", "old-password-1", new, m.run)
        self.assertEqual(["old-password-1"], m.keys)

    def test_key_files_are_private_and_removed(self):
        with A.KeyFiles("secret-value") as (path,):
            self.assertEqual(0o600, os.stat(path).st_mode & 0o777)
            self.assertEqual(0o700, os.stat(os.path.dirname(path)).st_mode & 0o777)
            folder = os.path.dirname(path)
        self.assertFalse(os.path.exists(folder))


class Accounts(unittest.TestCase):
    def test_logins_and_names_are_checked(self):
        for bad in ("Root", "root", "1abc", "a b", "../x", "x" * 40):
            with self.subTest(login=bad), self.assertRaises(A.AccountError):
                A.check_login(bad)
        self.assertEqual("bob", A.check_login("bob"))
        with self.assertRaisesRegex(A.AccountError, "one line"):
            A.add_account("bob2", "Bob\nEvil", "password-123", False, FakeMachine().run)

    def test_no_one_removes_themselves_or_the_first_account(self):
        with self.assertRaisesRegex(A.AccountError, "you are using"):
            A.remove_account("ada", False, "ada", ["ada"], FakeMachine().run)
        with self.assertRaisesRegex(A.AccountError, "first account"):
            A.remove_account("ada", False, "bob", ["ada"], FakeMachine().run)

    def test_the_helper_needs_pkexec_for_changes(self):
        if os.geteuid() == 0:
            self.skipTest("running as root")
        self.assertEqual(1, A.main(["password"]))


if __name__ == "__main__":
    unittest.main()
