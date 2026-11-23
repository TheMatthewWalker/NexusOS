"""Startup apps: the person's own autostart entries are listed, turned on and off, added and removed safely."""
import importlib.util
import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_startup", ROOT / "scripts/desktop/nexus_startup.py")
S = importlib.util.module_from_spec(spec)
spec.loader.exec_module(S)


class StartupApps(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.auto = self.tmp / "config/autostart"
        self.auto.mkdir(parents=True)
        self.apps = self.tmp / "share/applications"
        self.apps.mkdir(parents=True)
        self.env = mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": str(self.tmp / "config"),
                                                "XDG_DATA_HOME": str(self.tmp / "home-share"),
                                                "XDG_DATA_DIRS": str(self.tmp / "share")})
        self.env.start()
        (self.auto / "steam.desktop").write_text(
            "[Desktop Entry]\nType=Application\nName=Steam\nExec=steam -silent\n\n[Desktop Action Store]\nName=Store\nExec=steam store\n")
        (self.apps / "org.mozilla.firefox.desktop").write_text("[Desktop Entry]\nType=Application\nName=Firefox\nExec=firefox %u\n")
        (self.apps / "link.desktop").write_text("[Desktop Entry]\nType=Link\nName=A link\nURL=https://x\n")

    def tearDown(self):
        self.env.stop()
        shutil.rmtree(self.tmp)

    def test_turning_off_keeps_the_file_and_marks_it_hidden(self):
        self.assertEqual(S.listing(), [{"id": "steam", "name": "Steam", "on": True, "added": False}])
        self.assertIn("no longer open", S.set_on("steam", False)["message"])
        text = (self.auto / "steam.desktop").read_text()
        head = text.split("[Desktop Action")[0]
        self.assertIn("Hidden=true", head)
        self.assertIn("X-GNOME-Autostart-enabled=false", head)
        self.assertNotIn("Hidden", text.split("[Desktop Action")[1])          # only the main group changes
        self.assertFalse(S.listing()[0]["on"])
        S.set_on("steam", True)
        self.assertTrue(S.listing()[0]["on"])
        self.assertEqual((self.auto / "steam.desktop").read_text().count("Hidden="), 1)

    def test_add_copies_an_installed_app_and_remove_takes_only_those(self):
        r = S.add("org.mozilla.firefox.desktop")
        self.assertEqual(r["id"], "org.mozilla.firefox")
        self.assertIn("X-Nexus-Added=true", (self.auto / "org.mozilla.firefox.desktop").read_text())
        self.assertIn("error", S.remove("steam"))                            # it added itself
        self.assertIn("removed", S.remove("org.mozilla.firefox")["message"])
        self.assertFalse((self.auto / "org.mozilla.firefox.desktop").exists())

    def test_bad_names_and_non_apps_are_refused(self):
        for bad in ["../../etc/passwd", "", "x/y"]:
            self.assertIn("error", S.set_on(bad, True))
            self.assertIn("error", S.remove(bad))
        self.assertIn("error", S.add("../x.desktop"))
        self.assertIn("error", S.add("missing.desktop"))
        self.assertIn("error", S.add("link.desktop"))
        self.assertIn("error", S.add("firefox"))


if __name__ == "__main__":
    unittest.main()
