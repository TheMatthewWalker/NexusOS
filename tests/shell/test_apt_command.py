"""The apt command (nix/packages/nexus-apt.sh) with fake podman, distrobox and nexus.

Checks that it records what really changed for the timeline's Undo, that
--isolated never uses distrobox (which shares the home folder) and mounts only
its own home, and that it says plainly the normal container is not a sandbox.
Run: python3 tests/shell/test_apt_command.py
"""
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "nix/packages/nexus-apt.sh"

# A pretend container: a state file of "name version" lines.
FAKE_CONTAINER = r'''#!/usr/bin/env bash
echo "$(basename "$0") $*" >> "$FAKE_LOG"
state="$FAKE_STATE"
touch "$state"
if [ "$(basename "$0")" = podman ]; then
  case "$1" in
    container) [ -f "$FAKE_EXISTS" ]; exit ;;
    create|start) touch "$FAKE_EXISTS"; exit 0 ;;
    exec) shift; while [ "${1#-}" != "$1" ]; do case "$1" in --user|--env) shift 2 ;; *) shift ;; esac; done; shift ;;
  esac
else
  case "$1" in
    create) touch "$FAKE_EXISTS"; exit 0 ;;
    enter) shift 3 ;;
  esac
fi
[ "${1:-}" = sudo ] && shift
case "${1:-}" in
  true|sh) exit 0 ;;
  dpkg-query)
    shift 2
    for p in "$@"; do awk -v p="$p" '$1 == p { print $1, $2, "installed" }' "$state"; done ;;
  apt-get)
    shift
    op="$1"; shift
    for a in "$@"; do
      case "$a" in -*) continue ;; esac
      n="${a%%=*}"; v="9.9-1"; [ "$n" != "$a" ] && v="${a#*=}"
      grep -v "^$n " "$state" > "$state.new" || true; mv "$state.new" "$state"
      [ "$op" = install ] && echo "$n $v" >> "$state"
    done ;;
esac
exit 0
'''

FAKE_NEXUS = '''#!/usr/bin/env bash
echo "$*" >> "$FAKE_NEXUS_LOG"
'''


class AptCommand(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="nexus-apt-"))
        bin_dir = self.tmp / "bin"
        bin_dir.mkdir()
        for name in ["podman", "distrobox"]:
            (bin_dir / name).write_text(FAKE_CONTAINER)
            (bin_dir / name).chmod(0o755)
        (bin_dir / "nexus").write_text(FAKE_NEXUS)
        (bin_dir / "nexus").chmod(0o755)
        # Tests may run as root; the command refuses that, as it should.
        (bin_dir / "id").write_text("#!/usr/bin/env bash\necho 1000\n")
        (bin_dir / "id").chmod(0o755)
        self.env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", HOME=str(self.tmp / "home"),
                        XDG_DATA_HOME=str(self.tmp / "home/.local/share"),
                        FAKE_LOG=str(self.tmp / "calls"), FAKE_STATE=str(self.tmp / "state"),
                        FAKE_EXISTS=str(self.tmp / "exists"), FAKE_NEXUS_LOG=str(self.tmp / "nexus"))
        self.env.pop("NEXUS_UNDOES", None)

    def apt(self, *args, env=None):
        return subprocess.run(["bash", str(SCRIPT), *args], env=dict(self.env, **(env or {})),
                              capture_output=True, text=True, timeout=60)

    def recorded(self):
        path = self.tmp / "nexus"
        return path.read_text().splitlines() if path.exists() else []

    def test_install_and_remove_are_recorded_with_versions(self):
        self.assertEqual(self.apt("install", "htop", "--no-install-recommends").returncode, 0)
        self.assertEqual(self.recorded(), ["apps record --source apt --op install --id htop --box ubuntu --version 9.9-1"])
        # Installing it again changes nothing, so nothing new is recorded.
        self.apt("install", "htop")
        self.assertEqual(len(self.recorded()), 1)
        self.assertEqual(self.apt("remove", "htop", env={"NEXUS_UNDOES": "42"}).returncode, 0)
        self.assertEqual(self.recorded()[-1],
                         "apps record --source apt --op remove --id htop --box ubuntu --version 9.9-1 --undoes 42")

    def test_reinstall_at_a_version_records_that_version(self):
        self.apt("install", "htop=3.3.0-4")
        self.assertIn("--version 3.3.0-4", self.recorded()[-1])

    def test_isolated_uses_its_own_container_and_home_never_distrobox(self):
        result = self.apt("--isolated", "install", "curl")
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = (self.tmp / "calls").read_text()
        self.assertNotIn("distrobox", calls)
        create = next(l for l in calls.splitlines() if l.startswith("podman create"))
        home = self.tmp / "home"
        self.assertIn(f"--volume {home}/.local/share/nexus-apt/ubuntu-isolated-home:{home}", create)
        self.assertEqual(create.count("--volume"), 1)
        self.assertEqual(self.recorded(), ["apps record --source apt --op install --id curl --box ubuntu-isolated --version 9.9-1 --isolated"])

    def test_help_says_it_is_not_a_sandbox(self):
        out = self.apt("help").stdout
        self.assertIn("Ubuntu software runs as you and can read your home folder", out)
        self.assertIn("apt --isolated install X", out)

    def test_a_core_that_is_not_running_never_stops_apt(self):
        (self.tmp / "bin/nexus").write_text("#!/usr/bin/env bash\nexit 1\n")
        result = self.apt("install", "htop")
        self.assertEqual(result.returncode, 0)
        self.assertIn("Not added to the timeline", result.stderr)


if __name__ == "__main__":
    unittest.main()
