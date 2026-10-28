"""Checks for the Nexus shell build (shell/tools/build.py).

Run inside `nix develop .#desktop`: python3 -m unittest tests/shell/test_shell_build.py
"""
import importlib.util
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHELL = ROOT / "shell"


def load_build():
    spec = importlib.util.spec_from_file_location("shell_build", SHELL / "tools/build.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ShellBuild(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = Path(tempfile.mkdtemp(prefix="nexus-shell-"))
        cls.out = cls.tmp / "shell"
        args = ["python3", str(SHELL / "tools/build.py"), "--out", str(cls.out)]
        if not shutil.which("qsb"):
            args.append("--no-qsb")
        subprocess.run(args, check=True)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_every_qml_file_and_generated_input_is_present(self):
        for name in ["shell.qml", "NovaCore.qml", "CoreBridge.qml", "AppDirectory.qml", "Wallpaper.qml",
                     "SystemPanel.qml", "lib/orbit.mjs", "lib/describe.mjs", "lib/game.mjs", "lib/wifi.mjs", "lib/tokens.mjs", "lib/nova.frag", "lib/nova-noise-atlas.png"]:
            self.assertTrue((self.out / name).exists(), name)

    def test_every_referenced_icon_exists(self):
        icons = set()
        for qml in self.out.glob("*.qml"):
            icons |= set(re.findall(r'"icons/([a-z-]+)\.svg"', qml.read_text()))
            # Roles and planets build icon paths from ids: "icons/" + id + ".svg".
        icons |= {"files", "browser", "terminal", "studio", "models", "settings",
                  "network", "power", "bluetooth", "privacy", "time", "display", "sound"}
        for icon in icons:
            self.assertTrue((self.out / "icons" / f"{icon}.svg").exists(), icon)

    def test_nova_shader_is_valid_vulkan_glsl(self):
        tool = shutil.which("glslangValidator")
        if not tool:
            self.skipTest("glslangValidator not installed")
        frag = self.out / "lib/nova.frag"
        run = subprocess.run([tool, "-V", "-o", "/dev/null", str(frag)], capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertIn((SHELL / "qml/lib/nova-core.glsl").read_text(), frag.read_text())

    def test_noise_atlas_packs_the_study_field(self):
        from PIL import Image
        vol = load_build().noise_volume()
        atlas = Image.open(self.out / "lib/nova-noise-atlas.png")
        self.assertEqual((atlas.size, atlas.mode), ((528, 528), "L"))
        for x, y, z in [(0, 0, 0), (5, 9, 13), (63, 63, 63), (31, 2, 40)]:
            tx, ty = (z % 8) * 66, (z // 8) * 66
            self.assertEqual(atlas.getpixel((tx + 1 + x, ty + 1 + y)), vol[x + 64 * y + 4096 * z])
        self.assertEqual(atlas.getpixel((0, 1)), vol[63])
        study = (ROOT / "docs/design/spaceui/studies/nexus-core-nova.html").read_text()
        self.assertIn("seed * 1664525 + 1013904223", study)

    def test_suggestions_are_not_in_the_shell(self):
        """Keyword rules live in Core (POST /suggest); the shell must not carry its own."""
        orbit = (self.out / "lib/orbit.mjs").read_text()
        self.assertNotIn("suggestApps", orbit)
        self.assertNotIn("PATTERNS", orbit)

    def test_shell_safety_rules_hold_in_the_source(self):
        shell = (self.out / "shell.qml").read_text()
        self.assertIn("WlrLayer.Top", shell)
        self.assertNotIn("WlrLayer.Overlay", shell)              # never above lock screens
        self.assertIn("textFormat: Text.PlainText", shell)       # model output is plain text
        self.assertIn("Not installed", shell)                    # missing apps are not faked
        self.assertIn("Offline", shell)

    def test_core_gives_the_gpu_back_to_the_model(self):
        """The local model shares the GPU: the core must throttle and pause."""
        nova = (self.out / "NovaCore.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        self.assertIn("running: core.visible && !core.reduced && !core.paused", nova)
        self.assertIn("core.busy ? Math.min(core.steps, core.busySteps) : core.steps", nova)
        self.assertIn("const cap = core.busy ? core.busyFps", nova)
        self.assertIn("busy: root.modelBusy", shell)
        self.assertIn("paused: root.launcherPaused", shell)
        self.assertIn("IdleMonitor", shell)

    def test_shell_scales_down_to_fit_a_small_screen(self):
        """On a screen shorter or narrower than the layout, the whole surface scales down instead of cropping."""
        shell = (self.out / "shell.qml").read_text()
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn("fitScreen.height / root.lay.surface.height, fitScreen.width / root.lay.surface.width", shell)
        self.assertIn("implicitHeight: Math.round(root.lay.surface.height * fit)", shell)
        self.assertIn("scale: panel.fit; transformOrigin: Item.TopLeft", shell)
        # The hit regions are children of the scaled item, so input follows the scale.
        content = shell.index("id: content")
        self.assertLess(content, shell.index("id: fullHit"))
        self.assertLess(content, shell.index("id: launcherHit"))
        # A VM or remote desktop on Windows keeps the Super key: a terminal is also on Ctrl+Alt+T.
        self.assertIn("bind = CTRL ALT, T, exec,", desktop)

    def test_game_mode_frees_the_gpu_and_restores_the_desktop(self):
        """Feature 11: a fullscreen game unloads the model and drops blur and animations until it closes."""
        shell = (self.out / "shell.qml").read_text()
        bridge = (self.out / "CoreBridge.qml").read_text()
        cli = (ROOT / "src/NexusOS.Cli/Program.cs").read_text()
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn('import "lib/game.mjs" as G', shell)
        self.assertIn("G.activeGame(", shell)
        self.assertIn("t.fullscreen", shell)
        self.assertIn("unloadId = bridge.online ? bridge.unload() : -1", shell)
        self.assertIn('"keyword decoration:blur:enabled false ; keyword animations:enabled false"', shell)
        self.assertIn('"keyword decoration:blur:enabled " + blurConfigured', shell)
        self.assertIn('" ; keyword animations:enabled " + !reduced', shell)
        self.assertIn("G.toastText(true, freed)", shell)
        self.assertIn("G.toastText(false, null)", shell)
        self.assertIn("gameMode: root.gameMode", shell)                       # IPC state()
        self.assertIn("idle.isIdle || fullscreenActive || gameMode", shell)  # launcher core pauses
        self.assertNotIn("WlrLayer.Overlay", shell)                          # the toast is a compositor notification
        # The model reloads lazily: game mode never asks Core to load it.
        self.assertNotIn('send("load', bridge)
        self.assertIn('function unload() { return send("unload") }', bridge)
        self.assertIn('ev.type === "unloaded"', bridge)
        self.assertIn('case "unload":', cli)
        self.assertIn('"/inference/telemetry"', cli)
        self.assertIn('NEXUS_GAME_MODE,${if cfg.gameMode.enable then "1" else "0"}', desktop)
        self.assertIn("NEXUS_HYPRCTL,${config.programs.hyprland.package}/bin/hyprctl", desktop)
        self.assertIn('NEXUS_BLUR,${if cfg.blur then "1" else "0"}', desktop)
        self.assertRegex(desktop, r"gameMode\.enable = lib\.mkOption \{\s*type = lib\.types\.bool;\s*default = true;")

    def test_orbit_maths(self):
        self.run_node_test("orbit.test.mjs")

    def test_game_detection(self):
        self.run_node_test("game.test.mjs")

    def test_wifi_list_logic(self):
        self.run_node_test("wifi.test.mjs")

    def test_wifi_picker_uses_networkmanager_and_keeps_no_password(self):
        """The panel lists networks and connects through Quickshell's NetworkManager backend."""
        panel = (self.out / "SystemPanel.qml").read_text()
        self.assertIn('import "lib/wifi.mjs" as W', panel)
        self.assertIn("connectWithPsk(text)", panel)              # the password goes to NetworkManager
        self.assertIn("network.forget()", panel)
        self.assertIn("d.type === DeviceType.Wifi && d.nmManaged", panel)   # not an unmanaged adapter
        self.assertIn("function onConnectionFailed(reason)", panel)
        # The password box sits outside the rebuilt list, so typing survives a rescan.
        self.assertLess(panel.index("id: netRepeater"), panel.index("id: password"))
        self.assertIn("echoMode: reveal.checked ? TextInput.Normal : TextInput.Password", panel)
        self.assertIn("Qt.ImhSensitiveData", panel)
        # Scanning only while the panel is open.
        self.assertIn('property: "scannerEnabled"; value: win.expanded && win.wifiOn', panel)
        # The password box is cleared when it closes, and nothing is printed.
        self.assertIn('else password.text = ""', panel)
        self.assertNotIn("console.log", panel)

    def run_node_test(self, name):
        if not shutil.which("node"):
            self.skipTest("node not installed")
        test = self.out / "lib" / name
        shutil.copy(ROOT / "tests/shell" / name, test)
        run = subprocess.run(["node", "--test", str(test)], capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    def test_system_state_sentences(self):
        if not shutil.which("node"):
            self.skipTest("node not installed")
        test = self.out / "lib/describe.test.mjs"
        shutil.copy(ROOT / "tests/shell/describe.test.mjs", test)
        run = subprocess.run(["node", "--test", str(test)], capture_output=True, text=True)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    def test_system_panel_reads_real_state(self):
        """Plain-language state comes from the formatter, and disk usage from df."""
        panel = (self.out / "SystemPanel.qml").read_text()
        self.assertIn('import "lib/describe.mjs" as D', panel)
        self.assertIn('"df", "-P", "-B1", "/", "/home"', panel)
        self.assertIn("WlrLayer.Top", panel)
        self.assertNotIn("WlrLayer.Overlay", panel)


if __name__ == "__main__":
    unittest.main()
