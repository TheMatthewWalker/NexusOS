"""nexus-install's follow_release: pinning the installed system to the NexusOS release the stick was built from."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "nix/installer/nexus-install.sh").read_text()
BLOCK = SCRIPT[SCRIPT.index("# --- Releases"):SCRIPT.index("# --- end of releases")]
REV, NAR = "c" * 40, "sha256-AAAAexample="
LOCK = {"nodes": {"nexusos": {"inputs": {"nixpkgs": "nixpkgs"}, "locked": {"path": "./nexusos", "type": "path"},
                              "original": {"path": "./nexusos", "type": "path"}, "parent": []},
                  "nixpkgs": {"locked": {"type": "github", "owner": "NixOS", "repo": "nixpkgs", "rev": "d" * 40}},
                  "root": {"inputs": {"nexusos": "nexusos"}}}, "root": "root", "version": 7}
FLAKE = '{\n  inputs.nexusos.url = "path:./nexusos";\n  outputs = { self, nexusos }: { };\n}\n'


class FollowRelease(unittest.TestCase):
    def run_follow(self, copy_hash=NAR, evaluates=True, info=None):
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp, "nixos")
            (d / "nexusos").mkdir(parents=True)
            (d / "flake.nix").write_text(FLAKE)
            (d / "flake.lock").write_text(json.dumps(LOCK))
            info_file = Path(tmp, "release.json")
            info_file.write_text(json.dumps(info or {"owner": "TheMatthewWalker", "repo": "NexusOS", "ref": "release",
                                                     "rev": REV, "lastModified": 1795000000, "narHash": NAR}))
            # nix stands in: hash path answers COPY_HASH; eval succeeds if EVALUATES.
            script = ('set -euo pipefail\noffline=()\n'
                      'nix() { case "$*" in *"hash path"*) echo "$COPY_HASH" ;; *eval*) [ "$EVALUATES" = 1 ] ;; esac; }\n'
                      + BLOCK + f'\nif follow_release "{info_file}" "{d}"; then echo FOLLOWS; else echo COPY; fi\n')
            out = subprocess.run(["bash", "-c", script], capture_output=True, text=True,
                                 env={**os.environ, "COPY_HASH": copy_hash, "EVALUATES": "1" if evaluates else "0"})
            self.assertEqual(out.returncode, 0, out.stderr)
            return (out.stdout.strip().splitlines()[-1], (d / "flake.nix").read_text(), json.loads((d / "flake.lock").read_text()),
                    sorted(p.name for p in d.iterdir()))

    def test_an_exact_copy_follows_the_release_on_github(self):
        result, flake, lock, files = self.run_follow()
        self.assertEqual("FOLLOWS", result)
        self.assertIn('inputs.nexusos.url = "github:TheMatthewWalker/NexusOS/release";', flake)
        node = lock["nodes"]["nexusos"]
        self.assertEqual({"type": "github", "owner": "TheMatthewWalker", "repo": "NexusOS", "rev": REV, "narHash": NAR,
                          "lastModified": 1795000000}, node["locked"])
        self.assertEqual({"type": "github", "owner": "TheMatthewWalker", "repo": "NexusOS", "ref": "release"}, node["original"])
        self.assertNotIn("parent", node)
        self.assertEqual({"nixpkgs": "nixpkgs"}, node["inputs"])            # what the release brings stays locked
        self.assertEqual(LOCK["nodes"]["nixpkgs"], lock["nodes"]["nixpkgs"])
        self.assertEqual(["flake.lock", "flake.nix", "nexusos"], files)     # no leftovers

    def test_a_copy_that_differs_or_does_not_evaluate_is_installed_as_before(self):
        for kwargs in ({"copy_hash": "sha256-different="}, {"evaluates": False}):
            with self.subTest(**kwargs):
                result, flake, lock, files = self.run_follow(**kwargs)
                self.assertEqual("COPY", result)
                self.assertEqual(FLAKE, flake)
                self.assertEqual(LOCK, lock)
                self.assertEqual(["flake.lock", "flake.nix", "nexusos"], files)

    def test_odd_release_information_is_ignored(self):
        for bad in ({"rev": "not-a-rev"}, {"owner": "a b"}, {"ref": "-x"}, {"narHash": "md5-x"}, {"lastModified": "soon"}):
            info = {"owner": "TheMatthewWalker", "repo": "NexusOS", "ref": "release", "rev": REV, "lastModified": 1, "narHash": NAR, **bad}
            with self.subTest(bad=bad):
                self.assertEqual("COPY", self.run_follow(info=info)[0])

    def test_the_image_records_the_release_and_the_installer_uses_it(self):
        iso = (ROOT / "nix/installer/iso.nix").read_text()
        self.assertIn('environment.etc."nexusos-release.json"', iso)
        self.assertIn("nexusSource ? rev", iso)                        # only for an image built from a commit
        self.assertIn('nexusRelease = { owner = "TheMatthewWalker"; repo = "NexusOS"; ref = "release"; };',
                      (ROOT / "flake.nix").read_text())
        self.assertIn('follow_release "$release_info" /mnt/etc/nixos || true', SCRIPT)
        self.assertIn('cp -rP --no-preserve=ownership "$source_dir/."', SCRIPT)   # links stay links: same hash as the commit


if __name__ == "__main__":
    unittest.main()
