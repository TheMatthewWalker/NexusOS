import os
import sys
from pathlib import Path
import tempfile
import unittest
from unittest import mock
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

    def test_the_profiles_trial_boot_entry_is_the_same_baseline(self):
        generation = "/nix/store/" + "b" * 32 + "-nixos-system"
        trial = "/nix/store/" + "c" * 32 + "-nixos-system"
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "current").symlink_to(trial)
            (path / "profile").symlink_to(generation)
            # The store is not real here: resolve the specialisation link by hand.
            links = {generation + "/specialisation/guardian-trial": trial}
            with mock.patch("baseline_inspect.os.path.realpath", side_effect=lambda p: links.get(str(p), os.readlink(p) if os.path.islink(p) else str(p))):
                result = inspect(path / "current", path / "profile")
                self.assertEqual({"verified": True, "generationStorePath": generation, "profileStorePath": generation}, result)
                links.clear()  # Any other specialisation or system is still refused.
                self.assertFalse(inspect(path / "current", path / "profile")["verified"])
