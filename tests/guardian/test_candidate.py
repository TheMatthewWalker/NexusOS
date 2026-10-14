import importlib.util
import sys
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "guardian"))
from candidate import prepare

class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.changes = [{"option":"services.printing.enable","enabled":True}]

    def test_deterministic_and_non_executing(self):
        a = prepare(self.changes, "generation-42")
        b = prepare(self.changes, "generation-42")
        self.assertEqual(a, b)
        self.assertEqual(a["activation"], "prohibited")
        self.assertEqual(a["rollbackTarget"], "generation-42")
        self.assertNotIn("switch", a["operations"])
        self.assertIn("services.printing.enable = true;", a["nixModule"])

    def test_baseline_changes_identity(self):
        self.assertNotEqual(prepare(self.changes, "generation-42")["candidateId"],
                            prepare(self.changes, "generation-43")["candidateId"])

    def test_rejects_untrusted_operations(self):
        with self.assertRaises(ValueError):
            prepare([{"option":"imports","enabled":True}], "generation-42")
        with self.assertRaises(ValueError):
            prepare(self.changes, "../etc/passwd")
        with self.assertRaises(ValueError):
            prepare(self.changes, "")

if __name__ == "__main__":
    unittest.main()
