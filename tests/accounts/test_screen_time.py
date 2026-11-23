"""Screen time: hours and a daily limit for a child's account, enforced at login and each minute, failing open."""
import datetime
import importlib.util
import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_screen_time", ROOT / "scripts/accounts/nexus_screen_time.py")
T = importlib.util.module_from_spec(spec)
spec.loader.exec_module(T)

PEOPLE = {"parent": True, "kid": False}
AT = lambda h, m=0: datetime.datetime(2026, 11, 23, h, m)


class Fake:
    def __init__(self, sessions):
        self.sessions, self.calls = sessions, []

    def __call__(self, command, input_text=None):
        self.calls.append(command)
        if command[:2] == ["loginctl", "list-sessions"]:
            return 0, json.dumps(self.sessions), ""
        return 0, "", ""


class ScreenTime(unittest.TestCase):
    def setUp(self):
        self.base = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.base)

    def set(self, **request):
        return T.set_limits(dict({"login": "kid"}, **request), PEOPLE, self.base)

    def test_hours_and_minutes_decide_login(self):
        self.set(**{"from": "07:00", "to": "20:00", "minutes": 120})
        self.assertEqual(T.check("kid", AT(10), self.base), 0)
        self.assertEqual(T.check("kid", AT(6, 59), self.base), 1)
        self.assertEqual(T.check("kid", AT(20), self.base), 1)
        os.makedirs(os.path.join(self.base, "used"))
        Path(T.used_path("kid", AT(10).date(), self.base)).write_text("120")
        self.assertEqual(T.check("kid", AT(10), self.base), 1)
        self.assertEqual(T.check("parent", AT(3), self.base), 0)
        self.assertEqual(T.status("kid", AT(10), self.base)["why"], "Today's 120 minutes of screen time are used up.")

    def test_a_broken_limits_file_never_locks_anyone_out(self):
        os.makedirs(os.path.join(self.base, "limits"))
        for junk in ["{", "[]", '{"from": "25:00", "to": "x"}', '{"from": "20:00", "to": "07:00"}', '{"minutes": "lots"}']:
            Path(T.limits_path("kid", self.base)).write_text(junk)
            self.assertEqual(T.check("kid", AT(3), self.base), 0, junk)
        self.assertEqual(T.check("../etc/passwd", AT(3), self.base), 0)

    def test_setting_is_checked_and_admins_cannot_be_limited(self):
        for bad in [{"from": "07:00"}, {"from": "20:00", "to": "07:00"}, {"from": "7am", "to": "8pm"}, {"minutes": -1}, {"minutes": 5000}]:
            with self.assertRaises(T.LimitError, msg=bad):
                self.set(**bad)
        with self.assertRaisesRegex(T.LimitError, "Administrators"):
            T.set_limits({"login": "parent", "minutes": 60}, PEOPLE, self.base)
        with self.assertRaises(T.LimitError):
            T.set_limits({"login": "nobody", "minutes": 60}, PEOPLE, self.base)
        self.assertIn("60 minutes a day", self.set(minutes=60)["message"])
        self.assertIsNone(self.set(minutes=0)["limits"])
        self.assertFalse(os.path.exists(T.limits_path("kid", self.base)))

    def test_each_minute_counts_warns_and_signs_out(self):
        self.set(minutes=6)
        fake = Fake([{"user": "kid", "uid": 1001, "class": "user"}, {"user": "parent", "uid": 1000, "class": "user"},
                     {"user": "greeter", "uid": 990, "class": "greeter"}])
        self.assertEqual(T.tick(AT(9), self.base, fake), [("kid", "warned")])      # 1 used, 5 left
        for minute in range(1, 5):
            self.assertEqual(T.tick(AT(9, minute), self.base, fake), [])
        self.assertEqual(T.read_used("kid", AT(9).date(), self.base), 5)
        self.assertNotIn(["loginctl", "terminate-user", "kid"], fake.calls)
        self.assertEqual(T.tick(AT(9, 5), self.base, fake), [("kid", "out")])
        self.assertIn(["loginctl", "terminate-user", "kid"], fake.calls)
        self.assertNotIn(["loginctl", "terminate-user", "parent"], fake.calls)

    def test_old_days_are_forgotten(self):
        self.set(minutes=60)
        fake = Fake([{"user": "kid", "uid": 1001, "class": "user"}])
        T.tick(AT(9), self.base, fake)
        T.tick(AT(9) + datetime.timedelta(days=1), self.base, fake)
        self.assertEqual(os.listdir(os.path.join(self.base, "used")), ["kid-2026-11-24"])


if __name__ == "__main__":
    unittest.main()
