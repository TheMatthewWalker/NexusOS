import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location("guardian", pathlib.Path(__file__).resolve().parents[1] / "guardian" / "validate.py")
guardian = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guardian)

class GuardianTests(unittest.TestCase):
    def test_render(self):
        expression = guardian.render([{"option":"services.printing.enable","enabled":True}])
        self.assertIn("services.printing.enable = true;", expression)
    def test_injection_blocked(self):
        with self.assertRaises(ValueError):
            guardian.render([{"option":"imports","enabled":True}])
    def test_invalid_bool_blocked(self):
        with self.assertRaises(ValueError):
            guardian.render([{"option":"services.printing.enable","enabled":"true"}])
    def test_duplicates_blocked(self):
        with self.assertRaises(ValueError):
            guardian.render([{"option":"services.printing.enable","enabled":True},{"option":"services.printing.enable","enabled":False}])

if __name__ == "__main__":
    unittest.main()
