"""Get NexusOS (scripts/release): download a release's parts, check them, join them, check the image.

A fake GitHub serves a release on localhost: its API record, SHA256SUMS and the
parts of a small "image". The scripts are pointed at it with NEXUSOS_API.
"""
import hashlib
import http.server
import json
import os
import shutil
import subprocess
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SH = ROOT / "scripts/release/get-nexusos.sh"
PS1 = ROOT / "scripts/release/get-nexusos.ps1"
ISO = "nexusos-9.9.9-x86_64.iso"
SLIM = "nexusos-9.9.9-slim-x86_64.iso"


class FakeGitHub:
    def __init__(self, corrupt_first=None, missing=None, slim=False):
        self.dir = tempfile.TemporaryDirectory()
        root = Path(self.dir.name)
        self.image = os.urandom(300_000) + b"NexusOS"
        self.slim = os.urandom(150_000) + b"slim"
        size = 100_000
        (root / "files").mkdir()
        parts, sums = {}, []
        # As release.yml writes it: the full image and its parts, then the slim one and its parts.
        for iso, image in [(ISO, self.image)] + ([(SLIM, self.slim)] if slim else []):
            sums.append(f"{hashlib.sha256(image).hexdigest()}  {iso}")
            for i in range((len(image) + size - 1) // size):
                name, data = f"{iso}.part-{i:02d}", image[i * size:(i + 1) * size]
                parts[name] = data
                (root / "files" / name).write_bytes(data)
                sums.append(f"{hashlib.sha256(data).hexdigest()}  {name}")
        (root / "files" / "SHA256SUMS").write_text("\n".join(sums) + "\n")
        self.parts, self.served, self.corrupt_first, self.missing = parts, [], corrupt_first, missing
        fake = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                fake.served.append(self.path)
                if self.path in ("/releases/latest", "/releases/tags/v9.9.9"):
                    names = ["SHA256SUMS", *[n for n in parts if n != fake.missing]]
                    body = json.dumps({"tag_name": "v9.9.9", "assets": [
                        {"name": n, "browser_download_url": f"http://127.0.0.1:{fake.port}/download/{n}"} for n in names]}).encode()
                elif self.path.startswith("/download/"):
                    name = self.path.rsplit("/", 1)[1]
                    body = (root / "files" / name).read_bytes()
                    if name == fake.corrupt_first:
                        fake.corrupt_first = None       # wrong once, then right
                        body = b"x" * len(body)
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.dir.cleanup()


class GetNexusOS(unittest.TestCase):
    def setUp(self):
        self.out = tempfile.TemporaryDirectory()
        self.addCleanup(self.out.cleanup)

    def run_sh(self, fake, *args):
        env = {**os.environ, "NEXUSOS_API": f"http://127.0.0.1:{fake.port}/releases", "NEXUSOS_DIR": self.out.name}
        return subprocess.run(["sh", str(SH), *args], capture_output=True, text=True, env=env, timeout=120)

    def test_downloads_checks_and_joins_the_image_and_removes_the_parts(self):
        fake = FakeGitHub()
        self.addCleanup(fake.close)
        run = self.run_sh(fake)
        self.assertEqual(run.returncode, 0, run.stderr + run.stdout)
        self.assertEqual(fake.image, (Path(self.out.name) / ISO).read_bytes())
        self.assertEqual(sorted(p.name for p in Path(self.out.name).iterdir()), sorted([ISO, "SHA256SUMS"]))
        self.assertIn("checked against the release's SHA256SUMS", run.stdout)
        # Run again: already there, nothing downloaded but the checksums.
        fake.served.clear()
        again = self.run_sh(fake)
        self.assertIn("already here and checks out", again.stdout)
        self.assertFalse([p for p in fake.served if ".part-" in p])

    def test_a_part_that_does_not_check_out_is_downloaded_again(self):
        fake = FakeGitHub(corrupt_first=f"{ISO}.part-01")
        self.addCleanup(fake.close)
        run = self.run_sh(fake, "v9.9.9")
        self.assertEqual(run.returncode, 0, run.stderr + run.stdout)
        self.assertIn("did not check out; downloading it again", run.stdout)
        self.assertEqual(fake.image, (Path(self.out.name) / ISO).read_bytes())

    def test_a_release_missing_a_part_says_so_and_leaves_no_image(self):
        fake = FakeGitHub(missing=f"{ISO}.part-02")
        self.addCleanup(fake.close)
        run = self.run_sh(fake)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn("has no", run.stderr)
        self.assertFalse((Path(self.out.name) / ISO).exists())

    def test_the_full_image_unless_slim_is_asked_for(self):
        fake = FakeGitHub(slim=True)
        self.addCleanup(fake.close)
        run = self.run_sh(fake)
        self.assertEqual(run.returncode, 0, run.stderr + run.stdout)
        self.assertEqual(sorted(p.name for p in Path(self.out.name).iterdir()), sorted([ISO, "SHA256SUMS"]))
        self.assertFalse([p for p in fake.served if SLIM in p])
        slim = self.run_sh(fake, "--slim", "v9.9.9")
        self.assertEqual(slim.returncode, 0, slim.stderr + slim.stdout)
        self.assertEqual(fake.slim, (Path(self.out.name) / SLIM).read_bytes())

    def test_slim_from_a_release_without_one_says_so(self):
        fake = FakeGitHub()
        self.addCleanup(fake.close)
        run = self.run_sh(fake, "--slim")
        self.assertNotEqual(run.returncode, 0)
        self.assertIn("no slim image", run.stderr)

    @unittest.skipUnless(shutil.which("pwsh"), "PowerShell not installed")
    def test_the_windows_script_does_the_same(self):
        fake = FakeGitHub(corrupt_first=f"{ISO}.part-00", slim=True)
        self.addCleanup(fake.close)
        env = {**os.environ, "NEXUSOS_API": f"http://127.0.0.1:{fake.port}/releases", "NEXUSOS_DIR": self.out.name}
        run = subprocess.run(["pwsh", "-NoProfile", "-File", str(PS1)], capture_output=True, text=True, env=env, timeout=180)
        self.assertEqual(run.returncode, 0, run.stderr + run.stdout)
        self.assertEqual(fake.image, (Path(self.out.name) / ISO).read_bytes())
        self.assertEqual(sorted(p.name for p in Path(self.out.name).iterdir()), sorted([ISO, "SHA256SUMS"]))
        slim = subprocess.run(["pwsh", "-NoProfile", "-File", str(PS1)], capture_output=True, text=True,
                              env={**env, "NEXUSOS_EDITION": "slim"}, timeout=180)
        self.assertEqual(slim.returncode, 0, slim.stderr + slim.stdout)
        self.assertEqual(fake.slim, (Path(self.out.name) / SLIM).read_bytes())

    def test_the_double_click_wrappers_run_the_powershell_script_from_the_repository(self):
        for wrapper in ("Get-NexusOS.cmd", "Get-NexusOS-Slim.cmd"):
            cmd = (ROOT / "scripts/release" / wrapper).read_bytes()
            self.assertIn(b"\r\n", cmd)                               # Windows line ends
            self.assertIn(b"raw.githubusercontent.com/TheMatthewWalker/NexusOS/main/scripts/release/get-nexusos.ps1", cmd)
            self.assertIn(b"-ExecutionPolicy Bypass", cmd)
        self.assertIn(b"$env:NEXUSOS_EDITION='slim'", (ROOT / "scripts/release/Get-NexusOS-Slim.cmd").read_bytes())


if __name__ == "__main__":
    unittest.main()
