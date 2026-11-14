"""nexus-install's hardware scan, run against recorded lspci output and a fake /proc and /sys."""
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "nix/installer/nexus-install.sh").read_text()
BLOCK = SCRIPT[SCRIPT.index("# --- Hardware"):SCRIPT.index("# --- end of hardware scan")]

INTEL = "0000:00:02.0 VGA compatible controller [0300]: Intel Corporation Raptor Lake-P [Iris Xe Graphics] [8086:a7a0] (rev 04)"
RTX = "0000:01:00.0 VGA compatible controller [0300]: NVIDIA Corporation AD107M [GeForce RTX 4060 Max-Q / Mobile] [10de:28a0] (rev a1)"
AMD = "0000:06:00.0 VGA compatible controller [0300]: Advanced Micro Devices, Inc. [AMD/ATI] Phoenix1 [1002:15bf] (rev c4)"
RTX_3D = "0000:0a:00.0 3D controller [0302]: NVIDIA Corporation GA107M [GeForce RTX 3050 Mobile] [10de:25a2] (rev a1)"
GTX1060 = "0000:01:00.0 VGA compatible controller [0300]: NVIDIA Corporation GP106 [GeForce GTX 1060 6GB] [10de:1c03] (rev a1)"
AUDIO = "0000:01:00.1 Audio device [0403]: NVIDIA Corporation AD107 High Definition Audio Controller [10de:22be] (rev a1)"
VIRTIO = "0000:00:01.0 VGA compatible controller [0300]: Red Hat, Inc. Virtio 1.0 GPU [1af4:1050] (rev 01)"


def scan(lines, nvidia="auto", cpu="GenuineIntel", cpu_name="Intel(R) Core(TM) i5-1235U", mem_kib=16 * 1048576,
         battery=False, chassis=None, amd_vram_gib=None, model="", models=()):
    """Runs the scan; returns its variables as a dict."""
    with tempfile.TemporaryDirectory() as root:
        proc, sys, store = Path(root, "proc"), Path(root, "sys"), Path(root, "models")
        proc.mkdir()
        (proc / "cpuinfo").write_text(f"vendor_id\t: {cpu}\nmodel name\t: {cpu_name}\n")
        (proc / "meminfo").write_text(f"MemTotal:       {mem_kib} kB\n")
        if battery:
            (sys / "class/power_supply/BAT0").mkdir(parents=True)
        if chassis is not None:
            (sys / "class/dmi/id").mkdir(parents=True)
            (sys / "class/dmi/id/chassis_type").write_text(f"{chassis}\n")
        if amd_vram_gib is not None:
            card = sys / "class/drm/card1/device"
            card.mkdir(parents=True)
            (card / "vendor").write_text("0x1002\n")
            (card / "mem_info_vram_total").write_text(str(amd_vram_gib * 1073741824))
        for m in models:
            name, tag = m.split(":")
            f = store / "manifests/registry.ollama.ai/library" / name / tag
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_text("{}")
        names = ["nvidia_bus", "intel_bus", "amd_bus", "nvidia_name", "nvidia_open", "nvidia_driver", "graphics",
                 "gpu", "cpu", "memory_gib", "laptop", "amd_vram_gib", "ai_model", "ai_where", "large_model"]
        script = ("set -euo pipefail\nnvidia=%s model=%s\nfail() { echo \"$*\" >&2; exit 1; }\n"
                  "lspci() { cat <<'L'\n%s\nL\n}\n%s\n" % (nvidia, model, "\n".join(lines), BLOCK)
                  + "".join('printf "%%s\\n" "$%s"\n' % n for n in names))
        env = {"PATH": "/usr/bin:/bin:" + __import__("os").environ.get("PATH", ""),
               "NEXUS_PROC": str(proc), "NEXUS_SYS": str(sys), "NEXUS_MODELS": str(store)}
        out = subprocess.run(["bash", "-c", script], capture_output=True, text=True, check=True, env=env).stdout
        return dict(zip(names, out.split("\n")))   # each printed with its newline: an empty value stays


def detect(lines, nvidia="auto"):
    r = scan(lines, nvidia)
    return [r[k] for k in ("nvidia_bus", "intel_bus", "amd_bus", "nvidia_name", "nvidia_open", "nvidia_driver", "graphics")]


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

    def test_no_graphics_processor(self):
        nv, *_, graphics = detect([VIRTIO])
        self.assertEqual(nv, "")
        self.assertIn("no graphics processor", graphics)

    def test_no_nvidia_flag_skips_detection(self):
        nv, *_ = detect([INTEL, RTX], nvidia="off")
        self.assertEqual(nv, "")

    def test_bus_ids_match_the_nix_option_pattern(self):
        nv, intel, *_ = detect([INTEL, RTX])
        for bus in (nv, intel):
            self.assertRegex(bus, r"^PCI:[0-9]+:[0-9]+:[0-9]+$")
        module = (ROOT / "nix/modules/nvidia.nix").read_text()
        self.assertIn('strMatching "PCI:[0-9]+:[0-9]+:[0-9]+"', module)


class HardwareScan(unittest.TestCase):
    def test_intel_laptop_without_a_graphics_card(self):
        r = scan([INTEL], battery=True)
        self.assertEqual((r["gpu"], r["cpu"], r["laptop"], r["memory_gib"]), ("intel", "intel", "true", "16"))
        self.assertEqual(r["graphics"], "Intel Corporation Raptor Lake-P [Iris Xe Graphics] (built-in drivers)")
        self.assertEqual(r["ai_model"], "qwen3:4b")
        self.assertIn("on the processor", r["ai_where"])
        self.assertIn("downloaded once the computer is online", r["ai_where"])

    def test_the_chosen_model_comes_from_the_stick_when_it_is_there(self):
        r = scan([INTEL], models=["qwen3:4b", "qwen3:8b"])
        self.assertIn("from the USB stick", r["ai_where"])

    def test_laptop_found_by_chassis_without_a_battery(self):
        self.assertEqual(scan([INTEL], chassis=10)["laptop"], "true")
        self.assertEqual(scan([INTEL], chassis=3)["laptop"], "false")

    def test_memory_rounds_to_whole_gigabytes(self):
        self.assertEqual(scan([INTEL], mem_kib=16_190_000)["memory_gib"], "15")
        self.assertEqual(scan([INTEL], mem_kib=7_900_000)["memory_gib"], "8")

    def test_little_memory_gets_the_smallest_model(self):
        self.assertEqual(scan([INTEL], mem_kib=6 * 1048576)["ai_model"], "qwen3:1.7b")

    def test_nvidia_gets_the_big_model(self):
        r = scan([INTEL, RTX, AUDIO])
        self.assertEqual((r["gpu"], r["ai_model"]), ("nvidia", "qwen3:8b"))

    def test_no_nvidia_flag_still_says_the_card_is_there(self):
        r = scan([INTEL, RTX], nvidia="off")
        self.assertEqual(r["gpu"], "intel")
        self.assertIn("--no-nvidia", r["graphics"])

    def test_amd_card_with_its_own_memory_runs_the_model(self):
        r = scan([AMD], cpu="AuthenticAMD", amd_vram_gib=16)
        self.assertEqual((r["gpu"], r["cpu"], r["amd_vram_gib"], r["ai_model"]), ("amd", "amd", "16", "qwen3:8b"))

    def test_amd_built_in_gpu_shares_memory(self):
        r = scan([AMD], cpu="AuthenticAMD", amd_vram_gib=1)
        self.assertEqual(r["ai_model"], "qwen3:4b")

    def test_model_flag_wins_and_is_checked(self):
        self.assertEqual(scan([INTEL], model="qwen3:8b")["ai_model"], "qwen3:8b")
        with self.assertRaises(subprocess.CalledProcessError):
            scan([INTEL], model="'evil;rm'")

    def test_large_model_needs_16_gb_and_is_used_by_default_only_with_a_graphics_card(self):
        self.assertEqual(scan([INTEL], mem_kib=8 * 1048576)["large_model"], "")
        # A 16 GB computer reports a little under 16.
        laptop = scan([INTEL], mem_kib=16_190_000)["large_model"]
        self.assertIn("GPT-OSS 20B Heretic", laptop)
        self.assertIn("switch to it in Settings", laptop)
        self.assertIn("then used", scan([INTEL, RTX, AUDIO], mem_kib=16_190_000)["large_model"])
        module = (ROOT / "nix/modules/hardware.nix").read_text()
        self.assertIn("nexusos.largeModel.enable = lib.mkDefault (cfg.memoryGiB >= 15);", module)
        self.assertIn('"$memory_gib" -ge 15', BLOCK)

    def test_the_module_chooses_the_same_models(self):
        module = (ROOT / "nix/modules/hardware.nix").read_text()
        for m in ("qwen3:8b", "qwen3:4b", "qwen3:1.7b"):
            self.assertIn(f'"{m}"', module)
            self.assertIn(f'ai_model="{m}"', BLOCK)
        self.assertIn("cfg.memoryGiB >= 8", module)
        self.assertIn('"$memory_gib" -ge 8', BLOCK)
        self.assertIn("cfg.gpuMemoryGiB >= 6", module)
        self.assertIn('"$amd_vram_gib" -ge 6', BLOCK)


if __name__ == "__main__":
    unittest.main()
