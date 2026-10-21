import json
import re
import unittest

from support import ROOT
from guardian_schema import (SCHEMA, candidate_id, normalise, plan_id, render, require_baseline,
                             require_host_config, risk)

VECTORS = json.loads((ROOT / "schema" / "guardian-vectors.json").read_text())


class SharedSchemaTests(unittest.TestCase):
    def test_reproduces_cross_language_vectors(self):
        for case in VECTORS["cases"]:
            module = render(case["changes"])
            self.assertEqual(case["moduleText"], module)
            self.assertEqual(case["planId"], plan_id(module))
            self.assertEqual(case["candidateId"], candidate_id(VECTORS["baseline"], VECTORS["hostConfig"], module))
            self.assertEqual(case["risk"], risk(case["changes"])["level"])

    def test_rejected_vectors_are_rejected(self):
        for changes in VECTORS["rejected"]:
            with self.assertRaises(ValueError, msg=changes):
                render(changes)

    def test_order_does_not_change_identity(self):
        a = [{"option": "services.openssh.enable", "enabled": True}, {"option": "services.printing.enable", "enabled": False}]
        self.assertEqual(plan_id(render(a)), plan_id(render(list(reversed(a)))))

    def test_too_many_changes_rejected(self):
        with self.assertRaises(ValueError):
            normalise([{"option": "services.printing.enable", "enabled": True}] * (SCHEMA["maxChanges"] + 1))

    def test_baseline_must_be_observed_system_store_path(self):
        for bad in ("generation-42", "/nix/store/short-nixos-system-x", "/etc/passwd",
                    "/nix/store/" + "e" * 32 + "-nixos-system-x", "/nix/store/" + "1" * 32 + "-not-a-system"):
            with self.assertRaises(ValueError, msg=bad):
                require_baseline(bad)
        require_baseline(VECTORS["baseline"])

    def test_host_config_shape(self):
        require_host_config("path:/etc/nixos#nixosConfigurations.host-1")
        for bad in ("path:/etc/nixos", "x#nixosConfigurations.a;rm", "path:/etc/../root#nixosConfigurations.a", ""):
            with self.assertRaises(ValueError, msg=bad):
                require_host_config(bad)

    def test_schema_is_the_only_allowlist(self):
        # No Guardian implementation may carry its own copy of option names.
        names = list(SCHEMA["options"])
        offenders = []
        for path in list((ROOT / "scripts" / "guardian").glob("*.py")) + list((ROOT / "src").rglob("*.cs")):
            text = path.read_text()
            if any(re.search(re.escape('"' + name + '"'), text) for name in names):
                offenders.append(str(path.relative_to(ROOT)))
        self.assertEqual([], offenders)


if __name__ == "__main__":
    unittest.main()
