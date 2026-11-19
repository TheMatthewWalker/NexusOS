"""Settings on the installer image that once went wrong."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class ImageConfig(unittest.TestCase):
    def test_installed_systems_show_the_start_up_screen_and_the_image_can_build_it_offline(self):
        profile = (ROOT / "nix/profiles/nexusos.nix").read_text()
        self.assertIn("../modules/boot-splash.nix", profile)
        splash = (ROOT / "nix/modules/boot-splash.nix").read_text()
        self.assertIn('theme = "nexusos";', splash)
        theme = (ROOT / "nix/packages/nexus-plymouth.nix").read_text()
        self.assertIn("ModuleName=two-step", theme)                 # Plymouth's own password box
        self.assertIn("ImageDir=$theme", theme)                     # under share/plymouth/themes, which NixOS rewrites for the initrd
        # The initrd is built for each computer during the install: what it is made from is on the image.
        self.assertIn("++ referenceInitrdInputs", (ROOT / "nix/installer/iso.nix").read_text())
        self.assertIn("referenceInitrdInputs =", (ROOT / "flake.nix").read_text())
        # The VM test keeps it built in, and only does not start it on its serial console.
        self.assertIn('"plymouth.enable=0"', (ROOT / "nix/tests/nexus-installer.nix").read_text())

    def test_wifi_is_not_switched_off(self):
        # NetworkManager needs wpa_supplicant (networking.wireless.enable with
        # dbusControlled); forcing it off left the live USB with no Wi-Fi.
        iso = (ROOT / "nix/installer/iso.nix").read_text()
        self.assertIsNone(re.search(r"networking\.wireless\.enable\s*=\s*(lib\.)?mk\w*\s*false", iso))
        self.assertNotRegex(iso, r"networking\.wireless\.enable\s*=\s*false")

    def test_offline_install_keeps_the_stick_as_a_substituter(self):
        # nixos-install adds the installer's store with --extra-substituters, then
        # appends our flags: emptying substituters would drop it.
        script = (ROOT / "nix/installer/nexus-install.sh").read_text()
        self.assertNotIn('substituters ""', script)
        self.assertIn("offline=(--option connect-timeout 3)", script)

    def test_image_and_offline_test_share_the_build_tools(self):
        iso = (ROOT / "nix/installer/iso.nix").read_text()
        test = (ROOT / "nix/tests/nexus-installer.nix").read_text()
        self.assertIn("import ./offline-deps.nix pkgs", iso)
        self.assertIn("import ../installer/offline-deps.nix pkgs", test)


if __name__ == "__main__":
    unittest.main()
