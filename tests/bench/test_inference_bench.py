from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "bench"))
import nexus_inference_bench as bench  # noqa: E402


class FakeCore:
    """Simulates a model that uses 3 GiB of VRAM while loaded."""

    def __init__(self, unload_frees=True):
        self.loaded, self.unload_frees, self.calls = False, unload_frees, []

    def call(self, method, path, body=None):
        self.calls.append((method, path))
        if path == "/inference/unload":
            if self.unload_frees:
                self.loaded = False
            return 200, {"unloadRequested": True}
        if path == "/chat":
            self.loaded = True
            return 200, {"answer": "ok"}
        if path == "/inference/telemetry":
            gib = 1 << 30
            return 200, {"ramTotalBytes": 16 * gib, "ramAvailableBytes": (12 if self.loaded else 14) * gib,
                         "vramTotalBytes": 8 * gib, "vramFreeBytes": (5 if self.loaded else 8) * gib, "gpuSource": "nvidia-smi"}
        return 200, {}


class InferenceBenchTests(unittest.TestCase):
    def test_reclaim_is_measured_from_telemetry(self):
        result = bench.bench_provider(FakeCore(), "local", 2, "hi", 0)
        self.assertEqual(3 << 30, result["vramReclaimedBytes"])
        self.assertEqual(2 << 30, result["ramReclaimedBytes"])
        self.assertTrue(result["unloadAccepted"])
        self.assertEqual(2, len(result["warmRuns"]))

    def test_unreclaimed_memory_is_reported_not_hidden(self):
        result = bench.bench_provider(FakeCore(unload_frees=False), "local", 1, "hi", 0)
        self.assertEqual(0, result["vramReclaimedBytes"])

    def test_missing_gpu_telemetry_is_null(self):
        self.assertIsNone(bench.reclaimed({"vramTotalBytes": None, "vramFreeBytes": None}, {}, "vramTotalBytes", "vramFreeBytes"))

    def test_cold_start_follows_an_unload(self):
        core = FakeCore()
        bench.bench_provider(core, "local", 1, "hi", 0)
        first_chat = core.calls.index(("POST", "/chat"))
        self.assertIn(("POST", "/inference/unload"), core.calls[:first_chat])


if __name__ == "__main__":
    unittest.main()
