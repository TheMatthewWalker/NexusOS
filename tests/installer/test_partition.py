"""nexus-install's replace-a-partition plan (plan_partitions), against recordings of real disks.

As test_alongside: sfdisk, lsblk and the EFI partition's free space come from
tests/installer/disks/<case>/, and whether an NTFS partition holds Windows from
HOLDS (node=yes|no|unknown). Nothing touches a real disk.
"""
import json
import os
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "nix/installer/nexus-install.sh").read_text()
PLAN = SCRIPT[SCRIPT.index("# --- Alongside planning"):SCRIPT.index("# --- end of alongside planning")]
DISKS = ROOT / "tests/installer/disks"
GiB = 1073741824


def replace(case, disk="/dev/nvme0n1", holds=""):
    script = ("set -euo pipefail\nfail() { echo \"$*\" >&2; exit 1; }\n" + PLAN +
              'sfdisk() { [ -f "$REC/sfdisk.json" ] || return 1; cat "$REC/sfdisk.json"; }\n'
              'lsblk() { cat "$REC/lsblk.json"; }\n'
              'esp_free_bytes() { cat "$REC/esp_free"; }\n'
              'holds_windows() { local a; a="$(tr " " "\\n" <<<"$HOLDS" | awk -F= -v p="$1" \'$1 == p { print $2 }\')"; echo "${a:-unknown}"; }\n'
              f"plan_partitions {disk}\n")
    out = subprocess.run(["bash", "-c", script], capture_output=True, text=True,
                         env={**os.environ, "REC": str(DISKS / case), "HOLDS": holds})
    if out.returncode != 0:
        raise AssertionError(out.stderr)
    plan = json.loads(out.stdout)
    plan["by"] = {p["path"]: p for p in plan["partitions"]}
    return plan


@unittest.skipUnless(shutil.which("jq"), "jq not installed")
class ReplaceAPartition(unittest.TestCase):
    def test_windows_itself_and_its_recovery_partition_are_never_offered(self):
        plan = replace("windows-free-space", holds="/dev/nvme0n1p3=yes")
        self.assertFalse(plan["possible"])
        self.assertEqual(plan["esp"], "/dev/nvme0n1p1")
        # The EFI and Microsoft reserved partitions are not even listed.
        self.assertEqual(sorted(plan["by"]), ["/dev/nvme0n1p3", "/dev/nvme0n1p4"])
        self.assertIn("Windows itself (C:)", plan["by"]["/dev/nvme0n1p3"]["reason"])
        self.assertIn("recovery partition", plan["by"]["/dev/nvme0n1p4"]["reason"])

    def test_an_ntfs_partition_that_could_not_be_checked_is_refused(self):
        plan = replace("windows-free-space", holds="")
        self.assertFalse(plan["by"]["/dev/nvme0n1p3"]["possible"])
        self.assertIn("Fast startup", plan["by"]["/dev/nvme0n1p3"]["reason"])

    def test_an_ntfs_data_partition_can_be_replaced_with_a_warning(self):
        plan = replace("windows-free-space", holds="/dev/nvme0n1p3=no")
        p3 = plan["by"]["/dev/nvme0n1p3"]
        self.assertTrue(plan["possible"])
        self.assertTrue(p3["possible"], p3["reason"])
        self.assertEqual(p3["what"], "Windows (NTFS)")
        self.assertIn("deleted", p3["warning"])

    def test_an_earlier_nexusos_partition_can_be_replaced(self):
        plan = replace("earlier-nexusos", holds="/dev/nvme0n1p3=yes")
        p4 = plan["by"]["/dev/nvme0n1p4"]
        self.assertTrue(p4["possible"], p4["reason"])
        self.assertEqual(p4["what"], "NexusOS, from an earlier install")

    def test_a_linux_disk_offers_its_big_partitions_but_not_swap_or_what_is_mounted(self):
        plan = replace("linux-data", "/dev/sdb")
        self.assertTrue(plan["possible"])
        self.assertEqual(plan["esp"], "/dev/sdb1")
        ubuntu, swap, shared = plan["by"]["/dev/sdb2"], plan["by"]["/dev/sdb3"], plan["by"]["/dev/sdb4"]
        self.assertTrue(ubuntu["possible"])
        self.assertEqual((ubuntu["what"], ubuntu["label"], ubuntu["size"]), ("Linux (ext4)", "ubuntu", 200 * GiB))
        self.assertFalse(swap["possible"])
        self.assertIn("needs at least 32 GB", swap["reason"])
        self.assertFalse(shared["possible"])
        self.assertIn("in use", shared["reason"])

    def test_disks_it_cannot_share_say_why(self):
        for case, disk, words in [("blank", "/dev/vda", "no partitions"), ("mbr", "/dev/sda", "GPT"),
                                  ("two-esps", "/dev/nvme0n1", "2 EFI system partitions"),
                                  ("esp-too-small", "/dev/nvme0n1", "needs 200 MB")]:
            with self.subTest(case=case):
                plan = replace(case, disk)
                self.assertFalse(plan["possible"])
                self.assertIn(words, plan["reason"])
                self.assertEqual(plan["partitions"], [])

    def test_the_install_formats_only_that_partition_and_shares_the_efi_partition(self):
        block = SCRIPT[SCRIPT.index('elif [ -n "$partition" ]; then\n  # Only this partition'):SCRIPT.index("# Encryption: LUKS2")]
        branch = block.split("\nelse\n", 1)[0]
        self.assertIn('root_part="$partition" boot_part="$part_esp"', branch)
        self.assertIn('wipefs -af "$root_part"', branch)
        for destructive in ("mklabel", 'wipefs -af "$disk"', "mkfs", "--delete"):
            self.assertNotIn(destructive, branch)
        # Checked against the plan before anything changes.
        self.assertLess(SCRIPT.index('fail "cannot replace $partition'), SCRIPT.index("# --- Partition and format"))
        self.assertIn("--partition PART", SCRIPT[SCRIPT.index("usage() {"):SCRIPT.index("EOF\n}")])


if __name__ == "__main__":
    unittest.main()
