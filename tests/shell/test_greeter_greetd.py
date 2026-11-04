"""The login screen against a stand-in greetd (fake_greetd.py), typed with a virtual keyboard.

Needs sway, quickshell and python's pywayland, so run it inside the desktop shell:
    nix develop .#desktop -c python3 -m unittest tests/shell/test_greeter_greetd.py
Elsewhere it is skipped.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from fake_greetd import FakeGreetd  # noqa: E402

HAVE_TOOLS = all(shutil.which(t) for t in ("sway", "quickshell")) and os.environ.get("SPIKE_MESA")


@unittest.skipUnless(HAVE_TOOLS, "needs sway and quickshell (nix develop .#desktop)")
class GreeterAgainstGreetd(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="nexus-greeter-"))
        run = self.tmp / "run"
        run.mkdir(mode=0o700)
        mesa = os.environ["SPIKE_MESA"]
        self.env = dict(os.environ, XDG_RUNTIME_DIR=str(run), HOME=str(self.tmp), LIBGL_ALWAYS_SOFTWARE="1",
                        __EGL_VENDOR_LIBRARY_FILENAMES=f"{mesa}/share/glvnd/egl_vendor.d/50_mesa.json",
                        QT_QPA_PLATFORM="wayland")
        self.procs, self.files = [], []
        (self.tmp / "sway.conf").write_text("output HEADLESS-1 resolution 1280x720\ndefault_border none\n")
        self.spawn(["sway", "-c", str(self.tmp / "sway.conf")],
                   dict(self.env, WLR_BACKENDS="headless", WLR_RENDERER="pixman", WLR_LIBINPUT_NO_DEVICES="1"))
        for _ in range(200):
            socks = [p for p in run.glob("wayland-*") if p.suffix != ".lock"]
            if socks:
                break
            time.sleep(0.1)
        self.env["WAYLAND_DISPLAY"] = socks[0].name
        build = self.tmp / "shell"
        subprocess.run([sys.executable, str(ROOT / "shell/tools/build.py"), "--out", str(build), "--no-qsb"],
                       check=True, capture_output=True)
        self.build = build

    def tearDown(self):
        for p in self.procs:
            try:
                os.killpg(p.pid, 15)
            except ProcessLookupError:
                pass
        for p in self.procs:
            try:
                p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                p.kill()
                p.wait()
            for stream in (p.stdin, p.stdout):
                if stream:
                    stream.close()
        for f in self.files:
            f.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def spawn(self, cmd, env, log=None):
        out = subprocess.DEVNULL
        if log:
            out = open(log, "w")
            self.files.append(out)
        p = subprocess.Popen(cmd, env=env, stdout=out, stderr=subprocess.STDOUT, start_new_session=True)
        self.procs.append(p)
        return p

    def start_greeter(self, greetd):
        self.log = self.tmp / "greeter.log"
        self.heartbeat = self.tmp / "alive"
        self.spawn(["quickshell", "-p", str(self.build / "greeter.qml")],
                   dict(self.env, GREETD_SOCK=greetd.path, NEXUS_GREETER_USER="alice", NEXUS_GREETER_NAME="Alice",
                        NEXUS_GREETER_SESSION="true", NEXUS_GREETER_HEARTBEAT=str(self.heartbeat)), self.log)
        self.wait_for("password box has the keyboard")
        self.keys = subprocess.Popen([sys.executable, str(ROOT / "scripts/spike/vinput.py")],
                                     env=dict(self.env, SPIKE_OUTPUT="1280x720"), stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, text=True, start_new_session=True)
        self.procs.append(self.keys)
        self.assertEqual(self.keys.stdout.readline().strip(), "ready")
        # A new keyboard makes sway hand the focus round again: wait until the box has it back.
        end = time.time() + 30
        while time.time() < end:
            lines = [l for l in self.log.read_text().splitlines() if "password box" in l]
            if len(lines) >= 1 and "has the keyboard" in lines[-1] and time.time() > end - 27:
                break
            time.sleep(0.3)
        time.sleep(1)

    def wait_for(self, text, timeout=90):
        end = time.time() + timeout
        while time.time() < end:
            if self.log.exists() and text in self.log.read_text():
                return
            time.sleep(0.3)
        self.fail(f"greeter never logged {text!r}:\n" + self.log.read_text()[-3000:])

    def type(self, text):
        for cmd in (f"type {text}", "key Return"):
            self.keys.stdin.write(cmd + "\n")
            self.keys.stdin.flush()
            self.keys.stdout.readline()
        time.sleep(0.8)

    def test_expired_password_is_changed_and_the_login_carries_on(self):
        greetd = FakeGreetd(str(self.tmp / "greetd.sock"), "alice", "old-password", expired=True)
        self.start_greeter(greetd)
        self.assertTrue(self.heartbeat.exists(), "the greeter writes the watchdog's heartbeat")
        self.type("old-password")
        self.wait_for("the password has expired")
        # The current password is already in its box. A short one is refused here, before PAM.
        self.type("short")
        self.type("short")
        time.sleep(1)
        self.assertNotIn("changed", greetd.events)
        # Shorter than 10: the person types it again, then twice the good one.
        self.type("a-new-password")
        self.type("a-new-password")
        self.wait_for("password changed")
        self.wait_for("launched; exiting")
        self.assertEqual(greetd.password, "a-new-password")
        self.assertEqual(greetd.started, ["true"])
        self.assertIn("changed", greetd.events)
        self.assertNotIn("a-new-password", self.log.read_text())   # never logged
        self.assertNotIn("old-password", self.log.read_text())

    def test_a_wrong_password_is_still_a_wrong_password(self):
        greetd = FakeGreetd(str(self.tmp / "greetd.sock"), "alice", "old-password")
        self.start_greeter(greetd)
        self.type("not-it")
        self.wait_for("login refused")
        self.assertNotIn("the password has expired", self.log.read_text())
        self.type("old-password")
        self.wait_for("launched; exiting")


if __name__ == "__main__":
    unittest.main()
