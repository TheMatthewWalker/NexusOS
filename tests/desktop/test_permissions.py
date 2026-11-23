"""App permissions: Flatpak metadata and the person's overrides become plain switches, changed only with overrides."""
import importlib.util
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("nexus_permissions", ROOT / "scripts/desktop/nexus_permissions.py")
P = importlib.util.module_from_spec(spec)
spec.loader.exec_module(P)

META = """[Application]
name=com.spotify.Client
runtime=org.freedesktop.Platform/x86_64/24.08

[Context]
shared=network;ipc;
sockets=x11;pulseaudio;
devices=all;
filesystems=home;xdg-music:ro;
"""
PORTAL = "Table\tObject\tApp\tPermissions\tData\ndevices\tcamera\tcom.spotify.Client\tno\t0x00\nbackground\tbackground\tcom.other.App\tno\t0x00\n"


class Fake:
    def __init__(self, override=""):
        self.calls, self.override = [], override

    def __call__(self, cmd, **kw):
        self.calls.append(cmd)
        out = {"--show-metadata": META, "--show": self.override}.get(cmd[-2] if len(cmd) > 2 else "", "")
        if cmd[:2] == ["flatpak", "permission-show"]:
            out = PORTAL
        if cmd[:2] == ["flatpak", "list"]:
            out = "com.spotify.Client\tSpotify\norg.gimp.GIMP\tGIMP\nnot an id\tx\n"
        return subprocess.CompletedProcess(cmd, 0, out, "")


class AppPermissions(unittest.TestCase):
    def test_what_the_app_asked_for_with_overrides_on_top(self):
        r = P.show("com.spotify.Client", Fake())
        on = {p["kind"]: p["on"] for p in r["permissions"]}
        self.assertEqual(on, {"network": True, "files": True, "devices": True, "camera": False, "background": True})
        r = P.show("com.spotify.Client", Fake("[Context]\nshared=!network;\nfilesystems=!home;\ndevices=!all;\n"))
        on = {p["kind"]: p["on"] for p in r["permissions"]}
        self.assertEqual((on["network"], on["files"], on["devices"]), (False, False, False))

    def test_switches_are_user_overrides_and_portal_settings(self):
        f = Fake()
        P.set_permission("com.spotify.Client", "network", False, f)
        P.set_permission("com.spotify.Client", "files", True, f)
        P.set_permission("com.spotify.Client", "camera", True, f)
        self.assertEqual(f.calls[0], ["flatpak", "override", "--user", "--unshare=network", "com.spotify.Client"])
        self.assertIn(["flatpak", "override", "--user", "--filesystem=home", "com.spotify.Client"], f.calls)
        self.assertEqual(f.calls[-1], ["flatpak", "permission-set", "devices", "camera", "com.spotify.Client", "yes"])
        for cmd in f.calls:
            self.assertNotIn("--system", cmd)

    def test_bad_ids_and_kinds_are_refused(self):
        for bad in ["../x", "spotify", "com.spotify.Client;rm -rf ~", ""]:
            self.assertIn("error", P.show(bad, Fake()))
            self.assertIn("error", P.set_permission(bad, "network", False, Fake()))
            self.assertIn("error", P.reset(bad, Fake()))
        self.assertIn("error", P.set_permission("com.spotify.Client", "everything", True, Fake()))

    def test_apps_are_listed_by_name(self):
        self.assertEqual([a["id"] for a in P.apps(Fake())], ["org.gimp.GIMP", "com.spotify.Client"])


if __name__ == "__main__":
    unittest.main()
