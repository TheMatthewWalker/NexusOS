import unittest

from support import BASELINE, CHANGES, HOST, OTHER_BASELINE
from candidate import prepare


class CandidateTests(unittest.TestCase):
    def test_deterministic_and_non_executing(self):
        a, b = prepare(CHANGES, BASELINE, HOST), prepare(CHANGES, BASELINE, HOST)
        self.assertEqual(a, b)
        self.assertEqual("prohibited", a["activation"])
        self.assertEqual(BASELINE, a["rollbackTarget"])
        self.assertEqual(["evaluate", "build", "vm-test"], a["operations"])
        self.assertIn("services.printing.enable = true;", a["nixModule"])

    def test_identity_binds_baseline_and_host(self):
        base = prepare(CHANGES, BASELINE, HOST)["candidateId"]
        self.assertNotEqual(base, prepare(CHANGES, OTHER_BASELINE, HOST)["candidateId"])
        self.assertNotEqual(base, prepare(CHANGES, BASELINE, "path:/srv#nixosConfigurations.lab")["candidateId"])
        self.assertEqual(prepare(CHANGES, BASELINE, HOST)["planId"], prepare(CHANGES, OTHER_BASELINE, HOST)["planId"])

    def test_rejects_untrusted_input(self):
        for changes, baseline, host in (([{"option": "imports", "enabled": True}], BASELINE, HOST),
                                        (CHANGES, "generation-42", HOST), (CHANGES, "../etc/passwd", HOST),
                                        (CHANGES, BASELINE, "evil")):
            with self.assertRaises(ValueError):
                prepare(changes, baseline, host)

    def test_elevated_risk_is_reported(self):
        candidate = prepare([{"option": "services.openssh.enable", "enabled": True}], BASELINE, HOST)
        self.assertEqual("elevated", candidate["risk"]["level"])
        self.assertTrue(candidate["risk"]["notes"])


if __name__ == "__main__":
    unittest.main()
