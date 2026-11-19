"""nexus-install's keyboard layouts: the guess from the time zone, the list, and refusing unknown ones."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "nix/installer/nexus-install.sh").read_text()
BLOCK = SCRIPT[SCRIPT.index("# --- Keyboard layouts"):SCRIPT.index("# --- end of keyboard layouts")]
KEYBOARDS = ROOT / "nix/installer/keyboards.json"
ZONETAB = "# comment\nGB\t+513030-0000731\tEurope/London\nDE\t+5230+01322\tEurope/Berlin\n" \
          "CH\t+4723+00832\tEurope/Zurich\nMX\t+1924-09909\tAmerica/Mexico_City\nUS\t+404251-0740023\tAmerica/New_York\n" \
          "AQ\t-7750+16636\tAntarctica/McMurdo\n"


def keyboard_shell(body):
    with tempfile.TemporaryDirectory() as tmp:
        tab = Path(tmp, "zone.tab")
        tab.write_text(ZONETAB)
        env = {**os.environ, "NEXUS_KEYBOARDS": str(KEYBOARDS), "NEXUS_ZONETAB": str(tab)}
        script = "set -euo pipefail\nsource_dir=/nonexistent\n" + BLOCK + "\n" + body
        return subprocess.run(["bash", "-c", script], capture_output=True, text=True, check=True, env=env).stdout.strip()


class Keyboard(unittest.TestCase):
    def test_the_guess_follows_the_time_zones_country(self):
        for zone, layout in [("Europe/London", "gb"), ("Europe/Berlin", "de"), ("Europe/Zurich", "ch"),
                             ("America/Mexico_City", "latam"), ("America/New_York", "us"),
                             ("Antarctica/McMurdo", "us"), ("UTC", "us"), ("Nowhere/Else", "us")]:
            self.assertEqual(keyboard_shell(f'guessed_keyboard "{zone}"'), layout, zone)

    def test_names_console_keymaps_and_the_list(self):
        self.assertEqual(keyboard_shell('keyboard_name gb'), "English (UK)")
        self.assertEqual(keyboard_shell('keyboard_console gb'), "uk")
        listed = json.loads(keyboard_shell("keyboards_json"))
        self.assertEqual([k["name"] for k in listed], sorted(k["name"] for k in listed))
        self.assertIn({"id": "gb", "name": "English (UK)"}, listed)
        self.assertEqual(keyboard_shell('known_keyboard gb && echo yes'), "yes")
        self.assertEqual(keyboard_shell('known_keyboard "../x" || echo no'), "no")

    def test_every_layout_has_a_console_keymap_and_a_name_and_countries_are_not_shared(self):
        data = json.loads(KEYBOARDS.read_text())
        seen = {}
        for layout, entry in data.items():
            self.assertTrue(entry["console"] and entry["name"], layout)
            for country in entry["countries"]:
                self.assertNotIn(country, seen, f"{country} in {layout} and {seen.get(country)}")
                seen[country] = layout

    def test_the_installer_writes_the_layout_and_says_it(self):
        self.assertIn('nexusos.keyboard.layout = $(nix_string "$keyboard");', SCRIPT)
        self.assertIn('echo "  Keyboard:  $(keyboard_name "$keyboard")"', SCRIPT)
        # Asked before the password, and the console switched to it, so the password is typed with it.
        self.assertLess(SCRIPT.index("# --- Keyboard ---"), SCRIPT.index('read -rsp "Password: "'))
        self.assertIn('loadkeys "$(keyboard_console "$keyboard")"', SCRIPT)
        self.assertIn('fail "unknown keyboard layout $keyboard', SCRIPT)

    def test_the_module_reads_the_same_file(self):
        module = (ROOT / "nix/modules/keyboard.nix").read_text()
        self.assertIn("lib.importJSON ../installer/keyboards.json", module)
        self.assertIn("console.earlySetup = lib.mkDefault true;", module)
        self.assertIn("NEXUS_KEYBOARDS:=${../installer/keyboards.json}", (ROOT / "nix/packages/nexus-install.nix").read_text())


if __name__ == "__main__":
    unittest.main()
