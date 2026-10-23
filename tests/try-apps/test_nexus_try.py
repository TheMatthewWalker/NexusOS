"""Argument validation and sandbox construction for nexus-try, with nix and bwrap faked.

Runs the unwrapped script (nix/packages/nexus-try.sh). The fakes record their
arguments, so these tests check exactly what would be built and how the
sandbox would be set up, without needing Nix, namespaces or a store.
"""
import os
from pathlib import Path
import shutil
import socket
import stat
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("NEXUS_TRY_SCRIPT", ROOT / "nix" / "packages" / "nexus-try.sh"))
HASH = "0123456789abcdfghijklmnpqrsvwxyz"

FAKE_NIX = """#!BASH
printf '%s\\n' "$*" >> "$FAKE_LOG/nix"
for arg in "$@"; do
  case $arg in
    build) printf '%s\\n' $FAKE_OUTS; exit "${FAKE_BUILD_STATUS:-0}" ;;
    eval) printf '%s' "$FAKE_MAIN"; exit 0 ;;
  esac
done
exit 1
"""

FAKE_BWRAP = """#!BASH
printf '%s\\0' "$@" > "$FAKE_LOG/bwrap"
exit "${FAKE_STATUS:-0}"
"""


class NexusTryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = Path(self.temp.name)
        self.log = self.dir / "log"
        self.log.mkdir()
        bin_dir = self.dir / "bin"
        bin_dir.mkdir()
        for name, body in (("nix", FAKE_NIX), ("bwrap", FAKE_BWRAP)):
            (bin_dir / name).write_text(body.replace("BASH", shutil.which("bash"), 1))
            (bin_dir / name).chmod(0o755)
        self.store = self.dir / "store"
        self.out = self.store / f"{HASH}-hello-2.12"
        (self.out / "bin").mkdir(parents=True)
        (self.out / "bin" / "hello").write_text("#!/bin/sh\n")
        (self.out / "bin" / "hello").chmod(0o755)
        (self.out / "share").mkdir()
        self.home = self.dir / "home" / "alice"
        self.home.mkdir(parents=True)
        (self.home / "secret.txt").write_text("private")
        self.runtime = self.dir / "runtime"
        self.runtime.mkdir(mode=0o700)
        self.env = {
            "PATH": f"{bin_dir}:/usr/bin:/bin:{os.environ.get('PATH', '')}",
            "HOME": str(self.home), "NEXUS_TRY_STORE": str(self.store),
            "FAKE_LOG": str(self.log), "FAKE_OUTS": str(self.out), "FAKE_MAIN": "hello",
            "XDG_RUNTIME_DIR": str(self.runtime), "LANG": "C.UTF-8",
        }

    def tearDown(self):
        self.temp.cleanup()

    def run_try(self, *args, **env):
        return subprocess.run(["bash", str(SCRIPT), *args], env={**self.env, **env},
                              capture_output=True, text=True, timeout=30, check=False)

    def bwrap_args(self):
        raw = (self.log / "bwrap").read_bytes().split(b"\0")[:-1]
        return [part.decode() for part in raw]

    def nix_calls(self):
        path = self.log / "nix"
        return path.read_text().splitlines() if path.exists() else []

    @staticmethod
    def pairs(args, flag):
        return [(args[i + 1], args[i + 2]) for i, value in enumerate(args) if value == flag]

    def split(self, args):
        index = args.index("--")
        return args[:index], args[index + 1:]

    # --- validation -------------------------------------------------------

    def test_rejects_anything_but_a_plain_attribute(self):
        for bad in ("nixpkgs#hello", "github:evil/flake#x", "path:/tmp/x", "/nix/store/x-hello", "./hello",
                    "../hello", "hello;id", "hello$(id)", "hello`id`", "hello world", "hello\n", "'hello'",
                    "hello.", ".hello", "a..b", "a.b.c.d.e", "0ad", "let", "pkgs.or", "hel*lo", "héllo",
                    "a" * 65, "-hello", "hello|id", "hello&", "~hello", "hello>x"):
            with self.subTest(attr=bad):
                result = self.run_try(bad)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertEqual([], self.nix_calls())
                self.assertFalse((self.log / "bwrap").exists())

    def test_accepts_nested_attributes(self):
        for good in ("hello", "gnome-calculator", "python3Packages.ipython", "_1password-gui", "a.b.c.d"):
            with self.subTest(attr=good):
                self.assertEqual(0, self.run_try(good).returncode)

    def test_usage_errors(self):
        self.assertEqual(2, self.run_try().returncode)
        self.assertEqual(2, self.run_try("hello", "extra").returncode)
        self.assertEqual(2, self.run_try("--bogus", "hello").returncode)
        self.assertEqual(2, self.run_try("hello", "--file").returncode)
        self.assertEqual(0, self.run_try("--help").returncode)
        self.assertEqual([], self.nix_calls())

    def test_file_must_be_one_readable_regular_file(self):
        doc = self.home / "doc.txt"
        doc.write_text("x")
        self.assertEqual(2, self.run_try("--file", str(self.home / "missing"), "hello").returncode)
        self.assertEqual(2, self.run_try("--file", str(self.home), "hello").returncode)
        self.assertEqual(2, self.run_try("--file", str(doc), "--file", str(doc), "hello").returncode)
        self.assertEqual([], self.nix_calls())

    def test_home_inside_a_bound_path_is_refused(self):
        for home in ("/nix/store/x", "/etc/alice", "/run/user/1", "relative", "/"):
            with self.subTest(home=home):
                self.assertEqual(2, self.run_try("hello", HOME=home).returncode)

    # --- fetching ---------------------------------------------------------

    def test_fetches_with_nix_build_and_never_touches_a_profile(self):
        result = self.run_try("hello")
        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.nix_calls()
        self.assertIn("build --no-link --print-out-paths nixpkgs#hello", calls[0])
        self.assertTrue(all("profile" not in call and "install" not in call and "env" not in call.split() for call in calls))

    def test_build_failure_stops_before_the_sandbox(self):
        result = self.run_try("hello", FAKE_BUILD_STATUS="1")
        self.assertEqual(2, result.returncode)
        self.assertFalse((self.log / "bwrap").exists())

    def test_unexpected_output_paths_are_refused(self):
        result = self.run_try("hello", FAKE_OUTS="/etc/passwd")
        self.assertEqual(2, result.returncode)
        self.assertFalse((self.log / "bwrap").exists())

    # --- sandbox ----------------------------------------------------------

    def test_sandbox_isolation_flags(self):
        result = self.run_try("hello", "--", "--greeting", "hi there")
        self.assertEqual(0, result.returncode, result.stderr)
        options, command = self.split(self.bwrap_args())
        self.assertEqual([str(self.out / "bin" / "hello"), "--greeting", "hi there"], command)
        for flag in ("--unshare-all", "--die-with-parent", "--new-session", "--clearenv", "--share-net"):
            self.assertIn(flag, options)
        self.assertEqual("ALL", options[options.index("--cap-drop") + 1])
        self.assertIn((str(self.store), str(self.store)), self.pairs(options, "--ro-bind"))
        self.assertNotIn("--bind", options)
        # The home is a fresh directory on the sandbox's tmpfs root.
        self.assertEqual(str(self.home), options[options.index("--dir", options.index("--perms")) + 1])
        env = dict(self.pairs(options, "--setenv"))
        self.assertEqual(str(self.home), env["HOME"])
        self.assertTrue(env["PATH"].startswith(str(self.out / "bin") + ":"))

    def test_real_home_is_never_mounted(self):
        self.assertEqual(0, self.run_try("hello").returncode)
        options, _ = self.split(self.bwrap_args())
        sources = [src for flag in ("--ro-bind", "--ro-bind-try", "--bind", "--bind-try", "--dev-bind", "--dev-bind-try")
                   for src, _ in self.pairs(options, flag)]
        for src in sources:
            self.assertFalse(src == str(self.home) or src.startswith(str(self.home) + "/") or
                             str(self.home).startswith(src.rstrip("/") + "/"), src)

    def test_no_network_drops_the_network(self):
        self.assertEqual(0, self.run_try("--no-network", "hello").returncode)
        options, _ = self.split(self.bwrap_args())
        self.assertIn("--unshare-all", options)
        self.assertNotIn("--share-net", options)

    def test_file_is_handed_in_read_only(self):
        doc = self.home / "report.pdf"
        doc.write_text("x")
        result = self.run_try("hello", "--file", str(doc))
        self.assertEqual(0, result.returncode, result.stderr)
        options, _ = self.split(self.bwrap_args())
        self.assertIn((str(doc), str(self.home / "Trial" / "report.pdf")), self.pairs(options, "--ro-bind"))
        self.assertIn("~/Trial/report.pdf", result.stderr)

    def test_wayland_and_pipewire_are_passed_through(self):
        sockets = []
        for name in ("wayland-1", "pipewire-0"):
            sock = socket.socket(socket.AF_UNIX)
            sock.bind(str(self.runtime / name))
            sockets.append(sock)
        try:
            result = self.run_try("hello", WAYLAND_DISPLAY="wayland-1", DISPLAY=":0",
                                  DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/1/bus")
        finally:
            for sock in sockets:
                sock.close()
        self.assertEqual(0, result.returncode, result.stderr)
        options, _ = self.split(self.bwrap_args())
        binds = self.pairs(options, "--ro-bind")
        for name in ("wayland-1", "pipewire-0"):
            self.assertIn((str(self.runtime / name), str(self.runtime / name)), binds)
        env = dict(self.pairs(options, "--setenv"))
        self.assertEqual("wayland-1", env["WAYLAND_DISPLAY"])
        # No X11 and no session bus: only what is set explicitly survives --clearenv.
        self.assertNotIn("DISPLAY", env)
        self.assertNotIn("DBUS_SESSION_BUS_ADDRESS", env)

    def test_shell_mode_runs_a_shell_with_the_app_on_path(self):
        self.assertEqual(0, self.run_try("--shell", "hello", "--", "-c", "hello", NEXUS_TRY_SHELL="/bin/sh").returncode)
        _, command = self.split(self.bwrap_args())
        self.assertEqual(["/bin/sh", "-c", "hello"], command)

    def test_exit_reports_nothing_kept_and_offers_keep(self):
        result = self.run_try("hello", FAKE_STATUS="3")
        self.assertEqual(3, result.returncode)
        self.assertIn("Nothing was kept", result.stderr)
        self.assertIn("nexus try keep hello", result.stderr)

    def test_main_program_falls_back_to_the_only_executable(self):
        os.rename(self.out / "bin" / "hello", self.out / "bin" / "hi")
        self.assertEqual(0, self.run_try("hello", FAKE_MAIN="").returncode)
        _, command = self.split(self.bwrap_args())
        self.assertEqual([str(self.out / "bin" / "hi")], command)
        (self.out / "bin" / "other").write_text("")
        (self.out / "bin" / "other").chmod(stat.S_IRWXU)
        self.assertEqual(2, self.run_try("hello", FAKE_MAIN="nope").returncode)


if __name__ == "__main__":
    unittest.main()
