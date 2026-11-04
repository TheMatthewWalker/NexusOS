"""The image model download (scripts/images/nexus_image_model.py): pinned hash,
free-space check, resume, backoff and a daily limit. curl is faked; nothing is downloaded."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "images"))
import nexus_image_model as m  # noqa: E402

CONTENT = b"model-bytes" * 100
SHA = hashlib.sha256(CONTENT).hexdigest()
GIB = 1024 ** 3


class FakeCurl:
    """Writes the model to -o (resuming from what is there), or fails, as told."""

    def __init__(self, content=CONTENT, fail=False, stop_after=None, headers=""):
        self.content, self.fail, self.stop_after, self.headers = content, fail, stop_after, headers
        self.calls = []

    def __call__(self, command):
        self.calls.append(command)
        if "-sfIL" in command:
            return (0, self.headers) if self.headers else (22, "")
        if self.fail:
            return 22, ""
        out = Path(command[command.index("-o") + 1])
        have = out.read_bytes() if out.exists() else b""
        rest = self.content[len(have):]
        if self.stop_after is not None:
            rest = rest[:self.stop_after]
        out.write_bytes(have + rest)
        return (18 if len(have + rest) < len(self.content) else 0), ""


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dir = Path(self.temp.name)
        self.now = 1_800_000_000
        self.free = 50 * GIB

    def downloader(self, curl, sha=SHA, size=len(CONTENT), **kw):
        return m.Downloader(self.dir, "model.safetensors", "https://example.invalid/m.safetensors", sha, size,
                            runner=curl, clock=lambda: self.now, free=lambda _: self.free, **kw)

    def state(self):
        return json.loads((self.dir / "download.json").read_text())

    def test_downloads_checks_and_keeps_a_matching_model(self):
        curl = FakeCurl()
        self.assertEqual(0, self.downloader(curl).run_once())
        self.assertEqual(CONTENT, (self.dir / "model.safetensors").read_bytes())
        self.assertFalse((self.dir / "model.safetensors.part").exists())
        self.assertEqual(("ready", SHA), (self.state()["state"], self.state()["verified"]))
        self.assertIn("-C", curl.calls[0])                         # resumable
        self.assertFalse(any("-sfIL" in c for c in curl.calls))    # the hash is pinned, the host is not asked
        # Next time: nothing to do, nothing fetched.
        again = FakeCurl()
        self.assertEqual(0, self.downloader(again).run_once())
        self.assertEqual([], again.calls)

    def test_a_mismatch_is_deleted_and_never_kept(self):
        curl = FakeCurl(content=b"x" * len(CONTENT))
        self.assertEqual(1, self.downloader(curl).run_once())
        self.assertFalse((self.dir / "model.safetensors").exists())
        self.assertFalse((self.dir / "model.safetensors.part").exists())
        state = self.state()
        self.assertEqual(("waiting", "failed"), (state["state"], state["reason"]))
        self.assertIn("did not match its checksum", state["message"])

    def test_a_model_already_there_that_does_not_match_is_deleted(self):
        (self.dir / "model.safetensors").write_bytes(b"old")
        self.assertEqual(0, self.downloader(FakeCurl()).run_once())
        self.assertEqual(CONTENT, (self.dir / "model.safetensors").read_bytes())

    def test_without_room_for_the_model_plus_two_gib_nothing_is_fetched(self):
        self.free = len(CONTENT) + 2 * GIB - 1
        curl = FakeCurl()
        self.assertEqual(0, self.downloader(curl).run_once())
        self.assertEqual([], curl.calls)
        state = self.state()
        self.assertEqual(("waiting", "space"), (state["state"], state["reason"]))
        self.assertIn("Not enough free disk space", state["message"])
        self.assertEqual(len(CONTENT) + 2 * GIB, state["needed"])
        # Space is not a failed attempt: it starts as soon as there is room.
        self.free = 50 * GIB
        self.assertEqual(0, self.downloader(curl).run_once())
        self.assertEqual("ready", self.state()["state"])

    def test_an_interrupted_download_resumes_and_needs_room_only_for_the_rest(self):
        self.assertEqual(1, self.downloader(FakeCurl(stop_after=400)).run_once())
        self.assertEqual(400, (self.dir / "model.safetensors.part").stat().st_size)
        self.assertIn("resumes later", self.state()["message"])
        self.now += 11 * 60
        self.free = len(CONTENT) - 400 + 2 * GIB
        curl = FakeCurl()
        self.assertEqual(0, self.downloader(curl).run_once())
        self.assertEqual(CONTENT, (self.dir / "model.safetensors").read_bytes())

    def test_failures_back_off_exponentially_up_to_six_hours(self):
        waits = []
        for _ in range(8):
            downloader = self.downloader(FakeCurl(fail=True), max_per_day=100)
            self.assertEqual(1, downloader.run_once())
            waits.append(self.state()["nextTry"] - self.now)
            # Before the next try, a run does nothing at all.
            idle = FakeCurl()
            self.assertEqual(0, self.downloader(idle, max_per_day=100).run_once())
            self.assertEqual([], idle.calls)
            self.now = self.state()["nextTry"]
        self.assertEqual([600, 1200, 2400, 4800, 9600, 19200, 21600, 21600], waits)

    def test_at_most_a_few_attempts_a_day(self):
        self.now = 1_800_000_000 - 1_800_000_000 % 86400 + 60      # just after midnight UTC
        for _ in range(3):
            self.downloader(FakeCurl(fail=True), max_per_day=3).run_once()
            self.now = self.state()["nextTry"]
        curl = FakeCurl()
        self.assertEqual(0, self.downloader(curl, max_per_day=3).run_once())
        self.assertEqual([], curl.calls)
        state = self.state()
        self.assertEqual("daily-limit", state["reason"])
        self.assertEqual(0, state["nextTry"] % 86400)               # tomorrow, at midnight UTC
        self.now = state["nextTry"]
        self.assertEqual(0, self.downloader(FakeCurl(), max_per_day=3).run_once())
        self.assertEqual("ready", self.state()["state"])

    def test_with_no_pinned_hash_the_hosts_published_hash_is_required(self):
        headers = f'HTTP/2 200\r\nx-linked-etag: "{SHA}"\r\nx-linked-size: {len(CONTENT)}\r\n'
        self.assertEqual(0, self.downloader(FakeCurl(headers=headers), sha=None, size=None).run_once())
        self.assertEqual("ready", self.state()["state"])

    def test_with_no_hash_at_all_nothing_is_downloaded(self):
        (self.dir / "model.safetensors").unlink(missing_ok=True)
        curl = FakeCurl()
        self.assertEqual(1, self.downloader(curl, sha=None, size=None).run_once())
        self.assertFalse(any("-o" in c for c in curl.calls))
        self.assertIn("no checksum", self.state()["message"])

    def test_the_nix_module_pins_the_hash_and_size_and_uses_a_timer(self):
        nix = (ROOT / "nix/modules/images.nix").read_text()
        self.assertIn('default = "e869ac7d6942cb327d68d5ed83a40447aadf20e0c3358d98b2cc9e270db0da26";', nix)
        self.assertIn("default = 6938081905;", nix)
        self.assertIn("systemd.timers.nexus-image-model", nix)
        self.assertNotIn("Restart = \"on-failure\"", nix)
        self.assertNotIn("X-Linked-Etag is", nix)


if __name__ == "__main__":
    unittest.main()
