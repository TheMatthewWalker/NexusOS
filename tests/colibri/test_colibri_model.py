"""nexus-colibri-model: what it offers, when it refuses, and what it tells Core."""
import importlib.util
import io
import json
import os
import pathlib
import sys
import tempfile
import textwrap
import unittest
from contextlib import redirect_stdout
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "colibri" / "nexus_colibri_model.py"

# Stand-ins for colibri's setup_catalog and setup_download, with the same names and calls.
CATALOG = textwrap.dedent('''
    from collections import namedtuple
    E = namedtuple("E", "id name repo revision include exclude disk_gb ram_min_gb ram_good_gb size_class")
    ROWS = [E("qwen36-35b", "Qwen3.6-35B-A3B", "org/qwen", "main", (), (), 0.000002, 10, 20, "small"),
            E("deepseek-v4-flash", "DeepSeek V4 Flash", "org/ds", "main", (), (), 167, 16, 32, "large"),
            E("qwen-image-2.1", "Qwen-Image-2.1", "org/img", "main", (), (), 33, 12, 18, "small")]
    def by_id(i):
        return next((r for r in ROWS if r.id == i), None)
''')
DOWNLOAD = textwrap.dedent('''
    import json, os
    class DownloadError(RuntimeError): pass
    OFFLINE = os.environ.get("FAKE_OFFLINE") == "1"
    def is_complete(d):
        try: return json.load(open(os.path.join(d, ".colibri-download.json")))["complete"]
        except OSError: return False
    def list_repo_files(repo, revision, include=(), exclude=()):
        if OFFLINE: raise DownloadError("no network")
        return [{"path": "config.json", "size": 1000}, {"path": "model.bin", "size": 1000}]
    def bytes_present(d, files): return 0
    def download_repo(repo, revision, d, files, progress=None):
        os.makedirs(d, exist_ok=True)
        progress({"done": 2000, "total": 2000})
        json.dump({"complete": True}, open(os.path.join(d, ".colibri-download.json"), "w"))
''')


def load():
    spec = importlib.util.spec_from_file_location("nexus_colibri_model", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ColibriModelTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.lib = os.path.join(self.tmp.name, "lib")
        self.dir = os.path.join(self.tmp.name, "models")
        os.makedirs(self.lib)
        pathlib.Path(self.lib, "setup_catalog.py").write_text(CATALOG)
        pathlib.Path(self.lib, "setup_download.py").write_text(DOWNLOAD)
        for name in ("setup_catalog", "setup_download"):
            sys.modules.pop(name, None)
        self.addCleanup(self.tmp.cleanup)
        self.addCleanup(lambda: [sys.modules.pop(n, None) for n in ("setup_catalog", "setup_download")])
        self.addCleanup(lambda: sys.path.remove(self.lib) if self.lib in sys.path else None)
        self.m = load()

    def run_main(self, *args, memory=16 << 30):
        out = io.StringIO()
        with mock.patch.object(self.m, "memory_bytes", return_value=memory), redirect_stdout(out):
            code = self.m.main(["--lib", self.lib, "--dir", self.dir, *args])
        return code, out.getvalue()

    def status(self, model):
        return json.loads(pathlib.Path(self.dir, model + ".status.json").read_text())

    def test_lists_only_the_offered_chat_models_with_whether_each_fits(self):
        code, out = self.run_main("--list")
        self.assertEqual(code, 0)
        rows = {r["id"]: r for r in json.loads(out)}
        # The picture model is non-commercial; it is never offered.
        self.assertEqual(set(rows), {"qwen36-35b", "deepseek-v4-flash"})
        self.assertTrue(rows["qwen36-35b"]["fits"])
        self.assertEqual(rows["qwen36-35b"]["licence"], "Apache-2.0")
        self.assertTrue(rows["deepseek-v4-flash"]["streams"])

    def test_downloads_and_says_it_is_ready(self):
        code, _ = self.run_main("--model", "qwen36-35b")
        self.assertEqual(code, 0)
        self.assertEqual(self.status("qwen36-35b")["state"], "ready")
        self.assertEqual(self.status("qwen36-35b")["percent"], 100)
        # Run again: already whole, nothing to do.
        self.assertEqual(self.run_main("--model", "qwen36-35b")[0], 0)

    def test_too_little_memory_is_refused_with_the_numbers(self):
        code, _ = self.run_main("--model", "qwen36-35b", memory=8 << 30)
        self.assertEqual(code, 1)
        status = self.status("qwen36-35b")
        self.assertEqual(status["state"], "too-big")
        self.assertIn("needs 10 GB of memory", status["message"])

    def test_too_little_disk_is_refused(self):
        with mock.patch.object(self.m, "free_bytes", return_value=50 * 10**9):
            code, _ = self.run_main("--model", "deepseek-v4-flash", memory=32 << 30)
        self.assertEqual(code, 1)
        self.assertIn("172 GB free on the disk", self.status("deepseek-v4-flash")["message"])

    def test_offline_waits_to_try_again(self):
        with mock.patch.dict(os.environ, {"FAKE_OFFLINE": "1"}):
            sys.modules.pop("setup_download", None)
            code, _ = self.run_main("--model", "qwen36-35b")
        self.assertEqual(code, self.m.EXIT_RETRY)
        self.assertEqual(self.status("qwen36-35b")["state"], "waiting")

    def test_unknown_or_unoffered_models_are_refused(self):
        self.assertEqual(self.run_main("--model", "qwen-image-2.1")[0], 2)
        self.assertEqual(self.run_main("--model", "../etc")[0], 2)


if __name__ == "__main__":
    unittest.main()
