"""The graphical installer (shell/qml/Installer.qml, shell/qml/installer/, lib/installer.mjs) and how the live image runs it."""
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
QML = ROOT / "shell/qml"
INSTALLER = (QML / "Installer.qml").read_text()
LIVE = (QML / "live.qml").read_text()
ISO = (ROOT / "nix/installer/iso.nix").read_text()
SCRIPT = (ROOT / "nix/installer/nexus-install.sh").read_text()
PAGES = ["Field", "Card", "DiskPage", "YouPage", "TimePage", "SummaryPage", "ProgressPage"]


class GraphicalInstaller(unittest.TestCase):
    def test_logic(self):
        if not shutil.which("node"):
            self.skipTest("node not installed")
        with tempfile.TemporaryDirectory() as tmp:
            shutil.copy(QML / "lib/installer.mjs", tmp)
            shutil.copy(ROOT / "tests/shell/installer.test.mjs", tmp)
            run = subprocess.run(["node", "--test", str(Path(tmp, "installer.test.mjs"))], capture_output=True, text=True)
            self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    def test_every_page_is_there_and_used(self):
        for page in PAGES:
            self.assertTrue((QML / "installer" / f"{page}.qml").exists(), page)
        for page in ["DiskPage", "YouPage", "TimePage", "SummaryPage", "ProgressPage"]:
            self.assertIn(page + " {", INSTALLER)
        steps = re.search(r"STEPS = \[(.*?)\]", (QML / "lib/installer.mjs").read_text()).group(1)
        self.assertEqual(re.findall(r'"(\w+)"', steps), ["welcome", "disk", "you", "time", "summary", "install", "done"])

    def test_it_is_an_app_window_in_the_nexusos_style(self):
        self.assertRegex(INSTALLER, r"(?m)^NexusWindow \{")
        self.assertIn('title: "Install NexusOS"', INSTALLER)
        for qml in (QML / "installer").glob("*.qml"):
            text = qml.read_text()
            self.assertNotIn("FloatingWindow", text, qml.name)
            self.assertIn("theme", text, qml.name)                 # colours come from the theme
            # Imports go down, or to lib/ files, as elsewhere in the shell.
            for imp in re.findall(r'^import "([^"]+)"', text, re.M):
                self.assertTrue(imp.startswith("../lib/") and imp.endswith(".mjs"), (qml.name, imp))

    def test_it_runs_nexus_install_through_pkexec_with_flags_and_the_password_on_stdin(self):
        self.assertIn('["pkexec", win.tool, "--probe-json"]', INSTALLER)
        self.assertIn('["pkexec", tool].concat(I.installArgs(summaryState()))', INSTALLER)
        self.assertIn('write(win.password + "\\n"); stdinEnabled = false', INSTALLER)
        self.assertIn("I.parseLine(line)", INSTALLER)
        # The password is never part of a command line.
        for line in INSTALLER.splitlines():
            if re.search(r"command\s*[:=]", line):
                self.assertNotIn("password", line)
        # Every flag it passes is one nexus-install knows.
        flags = set(re.findall(r'"(--[a-z-]+)"', (QML / "lib/installer.mjs").read_text()))
        usage = SCRIPT[SCRIPT.index("usage() {"):SCRIPT.index("EOF\n}")]
        for flag in flags:
            self.assertIn("  " + flag, usage, flag)

    def test_a_failure_shows_the_error_and_the_log(self):
        progress = (QML / "installer/ProgressPage.qml").read_text()
        self.assertIn("page.p.error", progress)
        self.assertIn("page.p.tail", progress)
        self.assertIn("The full log is in", progress)
        self.assertIn('label: "Try again"', INSTALLER)

    def test_the_you_step_keeps_the_ten_character_rule(self):
        self.assertIn("MIN_PASSWORD = 10", (QML / "lib/installer.mjs").read_text())
        self.assertIn("${#password} -ge 10", SCRIPT)
        you = (QML / "installer/YouPage.qml").read_text()
        self.assertEqual(you.count("password: true"), 2)

    def test_alongside_is_offered_only_from_the_plan_and_erase_is_marked(self):
        disk = (QML / "installer/DiskPage.qml").read_text()
        self.assertIn("enabled: page.app.choices.alongside.possible", disk)
        self.assertIn("danger: true", disk)
        self.assertIn('"Erase and install"', INSTALLER)

    def test_the_live_session_opens_it_and_keeps_the_terminal_installer(self):
        self.assertIn("Installer { id: installer; theme: root.theme }", LIVE)
        self.assertIn("Component.onCompleted: installer.open()", LIVE)
        self.assertIn('label: "Install NexusOS"; onClicked: installer.open()', LIVE)
        self.assertIn("nexus-install-welcome", LIVE)

    def test_the_image_lets_only_the_live_user_run_only_nexus_install(self):
        self.assertIn("env = NEXUS_INSTALL,${nexus-install-as-root}", ISO)
        self.assertIn('action.id == "org.freedesktop.policykit.exec"', ISO)
        self.assertIn('subject.user == "nixos"', ISO)
        self.assertIn('action.lookup("program") == "${nexus-install-as-root}"', ISO)
        self.assertIn('exec ${nexus-install}/bin/nexus-install "$@"', ISO)

    def test_qml_parses(self):
        lint = shutil.which("qmllint") or next(iter(sorted(Path("/nix/store").glob("*-qtdeclarative-6*/bin/qmllint"))), None)
        if not lint:
            self.skipTest("qmllint not installed")
        files = [QML / "Installer.qml", QML / "live.qml", *sorted((QML / "installer").glob("*.qml"))]
        run = subprocess.run([str(lint), *map(str, files)], capture_output=True, text=True)
        # Quickshell's modules are not known to a bare qmllint; syntax errors are what matter here.
        errors = [l for l in (run.stdout + run.stderr).splitlines() if re.search(r"\[syntax\]|Syntax|Expected token|Unexpected token|SyntaxError", l)]
        self.assertEqual(errors, [])


if __name__ == "__main__":
    unittest.main()
