"""Settings, Default apps: apps are found from their desktop files, and mimeapps.list keeps everything else."""
import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_default_apps", ROOT / "scripts/desktop/nexus_default_apps.py")
A = importlib.util.module_from_spec(spec)
spec.loader.exec_module(A)


def entry(d, name, title, mime, extra=""):
    (d / name).write_text(f"[Desktop Entry]\nType=Application\nName={title}\nExec=x\nMimeType={mime}\n{extra}")


class DefaultApps(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.user, self.system = self.tmp / "user", self.tmp / "system"
        self.user.mkdir(); self.system.mkdir()
        entry(self.system, "firefox.desktop", "Firefox", "text/html;x-scheme-handler/http;x-scheme-handler/https;")
        entry(self.system, "chromium.desktop", "Chromium", "text/html;x-scheme-handler/http;x-scheme-handler/https;")
        entry(self.system, "vlc.desktop", "VLC", "video/mp4;audio/mpeg;")
        entry(self.system, "hidden.desktop", "Hidden", "video/mp4;", "NoDisplay=true\n")
        entry(self.system, "old.desktop", "Old", "video/mp4;")
        (self.user / "old.desktop").write_text("[Desktop Entry]\nType=Application\nName=Old\nHidden=true\n")
        self.apps = A.installed_apps([self.user, self.system])
        self.mimeapps = self.tmp / "mimeapps.list"

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_apps_that_can_open_each_kind_are_listed(self):
        self.assertNotIn("hidden.desktop", self.apps)
        self.assertNotIn("old.desktop", self.apps)          # hidden by the person's own copy
        cats = {c["id"]: c for c in A.listing(self.apps, [self.mimeapps])}
        self.assertEqual([a["name"] for a in cats["browser"]["apps"]], ["Chromium", "Firefox"])
        self.assertEqual(cats["browser"]["current"], "chromium.desktop")      # nothing chosen: the first
        self.assertEqual([a["id"] for a in cats["videos"]["apps"]], ["vlc.desktop"])
        self.assertEqual(cats["pdf"]["apps"], [])

    def test_setting_a_default_keeps_the_rest_of_the_file(self):
        self.mimeapps.write_text("[Added Associations]\ntext/html=chromium.desktop;\n\n[Default Applications]\n"
                                 "text/html=chromium.desktop\nimage/png=gimp.desktop\n")
        A.set_default("browser", "firefox.desktop", self.apps, self.mimeapps)
        text = self.mimeapps.read_text()
        self.assertIn("[Added Associations]\ntext/html=chromium.desktop;", text)
        self.assertIn("image/png=gimp.desktop", text)
        self.assertIn("text/html=firefox.desktop", text)
        self.assertIn("x-scheme-handler/https=firefox.desktop", text)
        self.assertNotIn("text/html=chromium.desktop\n", text)
        cats = {c["id"]: c for c in A.listing(self.apps, [self.mimeapps])}
        self.assertEqual(cats["browser"]["current"], "firefox.desktop")

    def test_a_new_file_and_refusals(self):
        A.set_default("videos", "vlc.desktop", self.apps, self.mimeapps)
        self.assertEqual(self.mimeapps.read_text(), "[Default Applications]\nvideo/mp4=vlc.desktop\n")
        for category, app in (("browser", "nope.desktop"), ("nothing", "vlc.desktop"), ("videos", "../x.desktop"), ("videos", "vlc")):
            with self.assertRaises(A.Problem):
                A.set_default(category, app, self.apps, self.mimeapps)


if __name__ == "__main__":
    unittest.main()
