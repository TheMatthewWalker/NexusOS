import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("bench", Path(__file__).resolve().parents[2] / "scripts" / "bench-ollama.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)

class BenchmarkTests(unittest.TestCase):
    def test_rate(self):
        self.assertEqual(20.0, bench.tokens_per_second({"eval_count":10,"eval_duration":500000000}))
        self.assertIsNone(bench.tokens_per_second({"response":"test"}))
    def test_url_allowlist(self):
        self.assertEqual("http://127.0.0.1:11434", bench.validate_url("http://127.0.0.1:11434"))
        with self.assertRaises(ValueError):
            bench.validate_url("http://example.net:11434")

if __name__ == "__main__":
    unittest.main()
