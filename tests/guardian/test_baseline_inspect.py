import sys
from pathlib import Path
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "guardian"))
from baseline_inspect import inspect

class BaselineInspectionTests(unittest.TestCase):
    def test_missing_symlinks_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            self.assertFalse(inspect(path / "missing", path / "profile")["verified"])

    def test_mismatched_generation_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "current").symlink_to("/nix/store/aaaaaaaa-current")
            (path / "profile").symlink_to("/nix/store/bbbbbbbb-profile")
            result = inspect(path / "current", path / "profile")
            self.assertFalse(result["verified"])

    def test_non_store_paths_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "current").symlink_to("/tmp/other")
            (path / "profile").symlink_to("/tmp/other")
            self.assertFalse(inspect(path / "current", path / "profile")["verified"])
