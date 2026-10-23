"""nexus-install's graphics detection, run against recorded lspci output."""
import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "nix/installer/nexus-install.sh").read_text()
BLOCK = SCRIPT[SCRIPT.index("# --- Graphics"):SCRIPT.index("# --- Confirm")]

INTEL = "0000:00:02.0 VGA compatible controller [0300]: Intel Corporation Raptor Lake-P [Iris Xe Graphics] [8086:a7a0] (rev 04)"
RTX = "0000:01:00.0 VGA compatible controller [0300]: NVIDIA Corporation AD107M [GeForce RTX 4060 Max-Q / Mobile] [10de:28a0] (rev a1)"
AMD = "0000:06:00.0 VGA compatible controller [0300]: Advanced Micro Devices, Inc. [AMD/ATI] Phoenix1 [1002:15bf] (rev c4)"
RTX_3D = "0000:0a:00.0 3D controller [0302]: NVIDIA Corporation GA107M [GeForce RTX 3050 Mobile] [10de:25a2] (rev a1)"
GTX1060 = "0000:01:00.0 VGA compatible controller [0300]: NVIDIA Corporation GP106 [GeForce GTX 1060 6GB] [10de:1c03] (rev a1)"
AUDIO = "0000:01:00.1 Audio device [0403]: NVIDIA Corporation AD107 High Definition Audio Controller [10de:22be] (rev a1)"
VIRTIO = "0000:00:01.0 VGA compatible controller [0300]: Red Hat, Inc. Virtio 1.0 GPU [1af4:1050] (rev 01)"


def detect(lines, nvidia="auto"):
    script = ("set -euo pipefail\nnvidia=%s\nlspci() { cat <<'L'\n%s\nL\n}\n%s\n"
              'printf "%%s|%%s|%%s|%%s|%%s|%%s|%%s\\n" "$nvidia_bus" "$intel_bus" "$amd_bus" '
              '"$nvidia_name" "$nvidia_open" "$nvidia_driver" "$graphics"\n') % (nvidia, "\n".join(lines), BLOCK)
    out = subprocess.run(["bash", "-c", script], capture_output=True, text=True, check=True).stdout.strip()
    return out.split("|")


class GpuDetection(unittest.TestCase):
    def test_intel_and_nvidia_laptop_uses_prime_offload(self):
        nv, intel, amd, name, open_, driver, graphics = detect([INTEL, RTX, AUDIO])
        self.assertEqual((nv, intel, amd), ("PCI:1:0:0", "PCI:0:2:0", ""))
        self.assertEqual(name, "GeForce RTX 4060 Max-Q / Mobile")
        self.assertEqual((open_, driver), ("true", "stable"))
        self.assertIn("PRIME offload", graphics)

    def test_amd_laptop_with_a_3d_controller_and_hex_bus(self):
        nv, intel, amd, name, *_ = detect([AMD, RTX_3D])
        self.assertEqual((nv, intel, amd), ("PCI:10:0:0", "", "PCI:6:0:0"))
        self.assertEqual(name, "GeForce RTX 3050 Mobile")

    def test_older_card_gets_the_legacy_closed_driver(self):
        nv, intel, amd, name, open_, driver, graphics = detect([GTX1060, AUDIO])
        self.assertEqual((open_, driver), ("false", "legacy_580"))
        self.assertEqual((intel, amd), ("", ""))
        self.assertEqual(graphics, "GeForce GTX 1060 6GB")

    def test_no_nvidia_card_changes_nothing(self):
        nv, *_, graphics = detect([VIRTIO])
        self.assertEqual(nv, "")
        self.assertIn("no NVIDIA card", graphics)

    def test_no_nvidia_flag_skips_detection(self):
        nv, *_ = detect([INTEL, RTX], nvidia="off")
        self.assertEqual(nv, "")

    def test_bus_ids_match_the_nix_option_pattern(self):
        nv, intel, *_ = detect([INTEL, RTX])
        for bus in (nv, intel):
            self.assertRegex(bus, r"^PCI:[0-9]+:[0-9]+:[0-9]+$")
        module = (ROOT / "nix/modules/nvidia.nix").read_text()
        self.assertIn('strMatching "PCI:[0-9]+:[0-9]+:[0-9]+"', module)


if __name__ == "__main__":
    unittest.main()
