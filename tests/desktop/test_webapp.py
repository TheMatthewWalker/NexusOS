"""Apps, Web apps: websites become launcher apps in the person's own folder, safely."""
import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_webapp", ROOT / "scripts/desktop/nexus_webapp.py")
W = importlib.util.module_from_spec(spec)
spec.loader.exec_module(W)


class WebApps(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.dir = self.tmp / "applications"

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_add_makes_an_app_window_desktop_file(self):
        r = W.add("Outlook", "https://outlook.office.com/mail/", self.dir)
        self.assertEqual(r["id"], "nexus-web-outlook")
        text = (self.dir / "nexus-web-outlook.desktop").read_text()
        self.assertIn("Exec=chromium --app=https://outlook.office.com/mail/\n", text)
        self.assertIn("Name=Outlook\n", text)
        self.assertEqual(W.listing(self.dir), [{"id": "nexus-web-outlook", "name": "Outlook", "url": "https://outlook.office.com/mail/"}])

    def test_percent_is_escaped_for_the_exec_line(self):
        W.add("Search", "https://example.com/?q=a%20b", self.dir)
        text = (self.dir / "nexus-web-search.desktop").read_text()
        self.assertIn("--app=https://example.com/?q=a%%20b\n", text)
        self.assertEqual(W.listing(self.dir)[0]["url"], "https://example.com/?q=a%20b")

    def test_unsafe_addresses_and_names_are_refused_or_cleaned(self):
        for url in ["file:///etc/passwd", "javascript:alert(1)", "https://a.com/ x", "https://a.com/'$(id)'",
                    "https://a.com/`id`", "https://a.com/\\x", "https://a.com/\"", "ftp://a.com", "https://a.com\n/x"]:
            self.assertIn("error", W.add("X", url, self.dir), url)
        r = W.add("Evil\nExec=rm -rf ~", "https://a.com/", self.dir)
        text = (self.dir / (r["id"] + ".desktop")).read_text()
        self.assertEqual([l for l in text.splitlines() if l.startswith("Exec=")], ["Exec=chromium --app=https://a.com/"])
        self.assertIn("error", W.add("   ", "https://b.com/", self.dir))

    def test_the_same_site_twice_is_one_app_and_names_do_not_collide(self):
        W.add("Mail", "https://mail.google.com/", self.dir)
        again = W.add("Gmail", "https://mail.google.com/", self.dir)
        self.assertEqual(again["id"], "nexus-web-mail")
        other = W.add("Mail", "https://outlook.office.com/", self.dir)
        self.assertEqual(other["id"], "nexus-web-mail-2")
        self.assertEqual(len(W.listing(self.dir)), 2)

    def test_remove_only_touches_web_apps(self):
        self.dir.mkdir(parents=True)
        (self.dir / "nexus-web-fake.desktop").write_text("[Desktop Entry]\nName=Not ours\nExec=x\n")
        (self.dir / "firefox.desktop").write_text("[Desktop Entry]\nName=Firefox\n")
        self.assertIn("error", W.remove("nexus-web-fake", self.dir))
        self.assertIn("error", W.remove("../firefox", self.dir))
        self.assertIn("error", W.remove("firefox", self.dir))
        r = W.add("Teams", "https://teams.microsoft.com/", self.dir)
        self.assertEqual(W.remove(r["id"], self.dir), {"message": "Teams was removed."})
        self.assertTrue((self.dir / "firefox.desktop").exists())
        self.assertEqual(W.listing(self.dir), [])


if __name__ == "__main__":
    unittest.main()
