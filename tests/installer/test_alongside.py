"""nexus-install's install-alongside planning, disk list, flags and --progress-json.

The planning runs against recordings of `sfdisk -J`, `parted -m unit B print
free` and `lsblk -J` (tests/installer/disks/<case>/), with the EFI system
partition's free space in esp_free. Nothing here touches a real disk: the
whole-script tests stop at flag checks, or at the root/UEFI/source checks.
"""
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = ROOT / "nix/installer/nexus-install.sh"
SCRIPT = SCRIPT_PATH.read_text()
PLAN = SCRIPT[SCRIPT.index("# --- Alongside planning"):SCRIPT.index("# --- end of alongside planning")]
PROGRESS = SCRIPT[SCRIPT.index("# --- Progress"):SCRIPT.index("# --- end of progress")]
DISKS = ROOT / "tests/installer/disks"
GiB, MiB = 1073741824, 1048576
NAMES = ["plan_ok", "plan_reason", "plan_esp", "plan_esp_free", "plan_start", "plan_size", "plan_free", "plan_systems", "plan_warnings"]


def run_bash(script, env=None):
    return subprocess.run(["bash", "-c", script], capture_output=True, text=True, env={**os.environ, **(env or {})})


def plan(case, disk="/dev/nvme0n1"):
    rec = DISKS / case
    script = ("set -euo pipefail\nfail() { echo \"$*\" >&2; exit 1; }\n" + PLAN +
              # The recordings stand in for the real commands (after the block, so they win).
              'sfdisk() { [ -f "$REC/sfdisk.json" ] || return 1; cat "$REC/sfdisk.json"; }\n'
              'parted() { cat "$REC/parted.txt"; }\n'
              'lsblk() { cat "$REC/lsblk.json"; }\n'
              'esp_free_bytes() { cat "$REC/esp_free"; }\n'
              f"plan_alongside {disk}\n"
              + "".join('printf "%%s\\x1f" "$%s"\n' % n for n in NAMES) +
              f'printf "\\n"; plan_json {disk}\n')
    out = run_bash(script, {"REC": str(rec)})
    if out.returncode != 0:
        raise AssertionError(out.stderr)
    first, js = out.stdout.split("\n", 1)
    r = dict(zip(NAMES, first.split("\x1f")))
    r["json"] = json.loads(js)
    return r


def partitions(case):
    return json.loads((DISKS / case / "sfdisk.json").read_text())["partitiontable"]["partitions"]


@unittest.skipUnless(shutil.which("jq"), "jq not installed")
class AlongsidePlanning(unittest.TestCase):
    def test_windows_with_free_space_goes_into_the_free_space(self):
        r = plan("windows-free-space")
        self.assertEqual(r["plan_ok"], "1", r["plan_reason"])
        self.assertEqual(r["plan_esp"], "/dev/nvme0n1p1")
        self.assertEqual(r["plan_systems"], "Windows")
        self.assertEqual(r["plan_warnings"], "")
        start, size = int(r["plan_start"]), int(r["plan_size"])
        # Aligned to 1 MiB, and entirely between Windows and its recovery partition.
        self.assertEqual(start % 2048, 0)
        self.assertEqual(size % 2048, 0)
        win, rec = partitions("windows-free-space")[2:4]
        self.assertGreaterEqual(start, win["start"] + win["size"])
        self.assertLessEqual(start + size, rec["start"])
        self.assertEqual(int(r["plan_free"]), 120 * GiB)
        self.assertIn("120 GB", r["plan_reason"])
        self.assertIn("Windows Boot Manager", r["plan_reason"])
        self.assertIn("not formatted", r["plan_reason"])
        self.assertEqual(r["json"], {"disk": "/dev/nvme0n1", "possible": True, "reason": r["plan_reason"], "esp": "/dev/nvme0n1p1",
                                     "espFreeBytes": 431 * MiB, "freeBytes": 120 * GiB, "systems": "Windows", "warnings": []})

    def test_no_free_space_says_to_make_room_in_windows(self):
        r = plan("windows-no-free-space")
        self.assertEqual(r["plan_ok"], "0")
        self.assertIn("no free space", r["plan_reason"])
        self.assertIn("Disk Management, Shrink volume", r["plan_reason"])
        self.assertIn("never shrinks", r["plan_reason"])
        self.assertFalse(r["json"]["possible"])

    def test_too_little_free_space_says_how_much_there_is(self):
        r = plan("windows-little-free-space")
        self.assertEqual(r["plan_ok"], "0")
        self.assertIn("20 GB", r["plan_reason"])
        self.assertIn("at least 32 GB", r["plan_reason"])
        self.assertIn("Shrink volume", r["plan_reason"])

    def test_a_small_efi_partition_is_refused_not_resized(self):
        r = plan("esp-too-small")
        self.assertEqual(r["plan_ok"], "0")
        self.assertIn("/dev/nvme0n1p1", r["plan_reason"])
        self.assertIn("68 MB free", r["plan_reason"])
        self.assertIn("200 MB", r["plan_reason"])

    def test_mbr_disk_is_refused(self):
        r = plan("mbr", "/dev/sda")
        self.assertEqual(r["plan_ok"], "0")
        self.assertIn("MBR", r["plan_reason"])

    def test_bitlocker_is_a_warning_not_a_refusal(self):
        r = plan("bitlocker")
        self.assertEqual(r["plan_ok"], "1", r["plan_reason"])
        self.assertEqual(r["plan_systems"], "Windows")
        self.assertIn("BitLocker recovery key", r["plan_warnings"])
        self.assertEqual(len(r["json"]["warnings"]), 1)

    def test_ambiguous_disks_are_refused(self):
        r = plan("two-esps")
        self.assertEqual(r["plan_ok"], "0")
        self.assertIn("2 EFI system partitions", r["plan_reason"])
        r = plan("earlier-nexusos")
        self.assertEqual(r["plan_ok"], "0")
        self.assertIn("already has a NexusOS partition", r["plan_reason"])

    def test_a_blank_disk_has_nothing_to_go_beside(self):
        r = plan("blank", "/dev/vda")
        self.assertEqual(r["plan_ok"], "0")
        self.assertIn("no partitions", r["plan_reason"])

    def test_the_disk_list_marks_the_installer_stick_and_skips_what_cannot_be_used(self):
        script = ("set -euo pipefail\n" + PLAN + 'lsblk() { cat "$REC"; }\ndisks_json\n')
        out = run_bash(script, {"REC": str(DISKS / "lsblk-disks.json")})
        self.assertEqual(out.returncode, 0, out.stderr)
        disks = {d["path"]: d for d in json.loads(out.stdout)}
        self.assertEqual(sorted(disks), ["/dev/nvme0n1", "/dev/sda", "/dev/sdb"])   # no zram, read-only or empty drives
        self.assertTrue(disks["/dev/sda"]["inUse"])                                  # /iso is mounted from it
        self.assertTrue(disks["/dev/sda"]["removable"])
        self.assertEqual(disks["/dev/sda"]["model"], "SanDisk 3.2Gen1")
        self.assertFalse(disks["/dev/nvme0n1"]["inUse"])
        self.assertFalse(disks["/dev/sdb"]["inUse"])                                 # only an earlier attempt's /mnt
        self.assertEqual(disks["/dev/nvme0n1"]["transport"], "nvme")

    def test_partitioning_alongside_adds_one_partition_and_formats_nothing_else(self):
        block = SCRIPT[SCRIPT.index("if [ \"$alongside\" -eq 1 ]; then\n  # One new partition"):SCRIPT.index("# Encryption: LUKS2")]
        alongside, erase = block.split("\nelse\n", 1)
        self.assertIn("sfdisk --append", alongside)
        self.assertIn('boot_part="$plan_esp"', alongside)
        for destructive in ("mkfs", "wipefs -af \"$disk\"", "mklabel", "wipefs -af \"$boot_part\""):
            self.assertNotIn(destructive, alongside)
        self.assertIn("mklabel gpt", erase)
        # A failed attempt removes the partition it added.
        self.assertIn('sfdisk --delete "$disk"', SCRIPT)


@unittest.skipUnless(shutil.which("jq"), "jq not installed")
class Flags(unittest.TestCase):
    def run_installer(self, *args):
        return subprocess.run(["bash", str(SCRIPT_PATH), *args], capture_output=True, text=True, timeout=30)

    def json_lines(self, out):
        return [json.loads(line) for line in out.splitlines() if line.strip()]

    def assert_refused(self, args, words, json_mode=False):
        r = self.run_installer(*args)
        self.assertEqual(r.returncode, 2, r.stderr)
        self.assertIn(words, r.stderr)
        if json_mode:
            events = self.json_lines(r.stdout)
            self.assertEqual(len(events), 1)
            self.assertEqual(events[0]["event"], "error")
            self.assertIn(words, events[0]["message"])
        else:
            self.assertEqual(r.stdout, "")

    def test_ambiguous_or_incomplete_flags_are_refused_before_anything_happens(self):
        self.assert_refused(["--alongside"], "--alongside needs --disk")
        self.assert_refused(["--alongside", "--user", "alice", "--yes"], "--alongside needs --disk")
        self.assert_refused(["--disk", "/dev/sda", "--disk", "/dev/sdb"], "--disk was given twice")
        self.assert_refused(["--disk"], "--disk needs a value")
        self.assert_refused(["--bogus"], "unknown option: --bogus")
        self.assert_refused(["--probe-json", "--log", "/tmp/x"], "keeps no log", json_mode=True)

    def test_progress_json_asks_no_questions(self):
        self.assert_refused(["--progress-json", "--disk", "/dev/sda", "--user", "alice", "--password-file", "-"], "needs --yes", json_mode=True)
        self.assert_refused(["--progress-json", "--yes", "--disk", "/dev/sda"], "needs --disk or --partition, --user and --password-file", json_mode=True)
        self.assert_refused(["--partition", "/dev/sda2", "--alongside"], "do not go together")
        self.assert_refused(["--progress-json", "--probe-json"], "do not go together", json_mode=True)

    def test_progress_json_reports_a_stop_as_one_error_object_with_the_log(self):
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp, "install.log")
            r = self.run_installer("--progress-json", "--yes", "--disk", "/nonexistent-disk", "--user", "alice",
                                   "--password-file", "-", "--source", "/nonexistent-source", "--log", str(log))
            self.assertEqual(r.returncode, 1)
            events = self.json_lines(r.stdout)            # standard output carries only JSON
            self.assertEqual(len(events), 1, r.stdout)
            ev = events[0]
            self.assertEqual(ev["event"], "error")
            self.assertEqual(ev["log"], str(log))
            self.assertRegex(ev["message"], "run me as root|UEFI|no NexusOS source")
            self.assertIsInstance(ev["tail"], list)
            self.assertIn("nexus-install started", log.read_text())
            self.assertIn(ev["message"], log.read_text())

    def test_probe_json_prints_only_json(self):
        r = self.run_installer("--probe-json", "--source", "/nonexistent-source")
        events = self.json_lines(r.stdout)
        self.assertEqual(len(events), 1, r.stdout)
        self.assertIn(events[0]["event"], ("probe", "error"))

    def test_human_output_stays_the_default(self):
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp, "install.log")
            r = self.run_installer("--source", "/nonexistent-source", "--log", str(log))
            self.assertNotIn('"event"', r.stdout + r.stderr)
            self.assertIn("The full log is in " + str(log), r.stderr + r.stdout)
            self.assertEqual(log.stat().st_mode & 0o777, 0o644)


@unittest.skipUnless(shutil.which("jq"), "jq not installed")
class ProgressFormat(unittest.TestCase):
    def test_steps_details_and_done_are_one_object_per_line(self):
        script = ("set -euo pipefail\njson=1 log=/tmp/x.log\nexec 3>&1 1>/dev/null\n"
                  'emit() { [ "$json" -eq 0 ] || jq -cn "$@" >&3; }\n'
                  'log_tail() { echo "[]"; }\n' + PROGRESS +
                  'step prepare "Preparing /dev/vda"\nstep install "Installing NexusOS"\n'
                  "printf 'copying path \"/nix/store/abc\"\\nbuilding \\\"x\\\"\\n' | jq -R -c --unbuffered '{event: \"detail\", text: .[0:200]}' >&3\n"
                  "emit '{event: \"done\"}'\n")
        out = run_bash(script)
        self.assertEqual(out.returncode, 0, out.stderr)
        events = [json.loads(line) for line in out.stdout.splitlines()]
        self.assertEqual(events[0], {"event": "step", "id": "prepare", "n": 1, "of": 6, "title": "Preparing /dev/vda"})
        self.assertEqual(events[1]["n"], 2)
        self.assertEqual([e["event"] for e in events[2:]], ["detail", "detail", "done"])
        self.assertEqual(events[3]["text"], 'building "x"')

    def test_the_step_ids_are_the_ones_the_graphical_installer_knows(self):
        ids = [line.split()[1] for line in SCRIPT.splitlines() if line.strip().startswith("step ") and not line.strip().startswith("step()")]
        self.assertEqual(ids, ["prepare", "format", "config", "install", "model", "password"])


if __name__ == "__main__":
    unittest.main()
