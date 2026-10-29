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
                     "OrbitalControls.qml", "TopBar.qml", "Settings.qml", "Apps.qml", "Code.qml", "Images.qml", "ActionCards.qml", "greeter.qml", "WifiModel.qml", "DesktopSettings.qml", "Nebula.qml",
                     "Search.qml", "Overview.qml", "FirstRun.qml", "Explain.qml", "MoveIn.qml", "lib/firstrun.mjs", "lib/bundles.mjs",
                     *[f"settings/{p}Page.qml" for p in ["Network", "Bluetooth", "Sound", "Display", "Power", "Desktop", "Privacy", "About", "Search"]],
                     "lib/orbit.mjs", "lib/describe.mjs", "lib/game.mjs", "lib/wifi.mjs", "lib/tokens.mjs", "lib/nova.frag", "lib/nova-noise-atlas.png"]:
            self.assertTrue((self.out / name).exists(), name)

    def test_every_referenced_icon_exists(self):
        icons = set()
        for qml in self.out.rglob("*.qml"):
            icons |= set(re.findall(r'icons/([a-z-]+)\.svg"', qml.read_text()))
            # Roles and planets build icon paths from ids: "icons/" + id + ".svg".
        icons |= {"files", "browser", "terminal", "studio", "models", "settings",
                  "network", "power", "bluetooth", "privacy", "time", "display", "sound", "desktop", "about"}
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
        # Afterwards, the person's own choices from Settings, Desktop come back.
        self.assertIn('"keyword decoration:blur:enabled " + desktop.blur', shell)
        self.assertIn('" ; keyword animations:enabled " + desktop.animations', shell)
        self.assertIn("DesktopSettings { id: desktop; gameMode: root.gameMode", shell)
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
        self.assertIn("NEXUS_HYPRCTL,${hyprctl}", desktop)
        self.assertIn('hyprctl = "${config.programs.hyprland.package}/bin/hyprctl";', desktop)
        self.assertIn('NEXUS_BLUR,${if cfg.blur then "1" else "0"}', desktop)
        self.assertRegex(desktop, r"gameMode\.enable = lib\.mkOption \{\s*type = lib\.types\.bool;\s*default = true;")

    def test_orbit_maths(self):
        self.run_node_test("orbit.test.mjs")

    def test_game_detection(self):
        self.run_node_test("game.test.mjs")

    def test_wifi_list_logic(self):
        self.run_node_test("wifi.test.mjs")

    def test_wifi_picker_uses_networkmanager_and_keeps_no_password(self):
        """Settings, Network lists networks and connects through Quickshell's NetworkManager backend."""
        model = (self.out / "WifiModel.qml").read_text()
        page = (self.out / "settings/NetworkPage.qml").read_text()
        self.assertIn('import "lib/wifi.mjs" as W', model)
        self.assertIn("connectWithPsk(text)", model)              # the password goes to NetworkManager
        self.assertIn("row.network.forget()", model)
        self.assertIn("d.type === DeviceType.Wifi && d.nmManaged", model)   # not an unmanaged adapter
        self.assertIn("function onConnectionFailed(reason)", model)
        # Scanning only while the Network page is showing.
        self.assertIn('property: "scannerEnabled"; value: wifi.scanning && wifi.on', model)
        self.assertIn("Component.onDestruction: wifi.scanning = false", page)
        # The password box sits outside the rebuilt list, so typing survives a rescan.
        self.assertLess(page.index("id: netRepeater"), page.index("id: password"))
        self.assertIn("echoMode: reveal.checked ? TextInput.Normal : TextInput.Password", page)
        self.assertIn("Qt.ImhSensitiveData", page)
        # The password box is cleared when it closes, and nothing is printed.
        self.assertIn('else password.text = ""', page)
        for text in (model, page):
            self.assertNotIn("console.log", text)

    def test_orbital_controls_are_shortcuts_to_settings(self):
        """The orbital controls sit bottom right and open Settings pages; the controls themselves are in Settings."""
        controls = (self.out / "OrbitalControls.qml").read_text()
        settings = (self.out / "Settings.qml").read_text()
        self.assertIn("anchors { bottom: true; right: true }", controls)
        self.assertIn("TapHandler { onTapped: win.openPage(modelData.page) }", controls)
        self.assertNotIn("audio.volume =", controls)                # no quick controls left in the panel
        self.assertNotIn("connectWithPsk", controls)
        self.assertIn("Nebula {", controls)
        self.assertIn('target: "settings"', settings)
        for page in ["network", "bluetooth", "sound", "display", "power", "desktop", "privacy", "about"]:
            self.assertIn(f'{{ id: "{page}", label:', settings)
        self.assertIn("WlrLayer.Top", controls)
        self.assertNotIn("WlrLayer.Overlay", controls)

    def test_desktop_settings_are_per_person_and_applied_with_hyprctl(self):
        desktop = (self.out / "DesktopSettings.qml").read_text()
        self.assertIn('path: desktop.configDir + "/desktop.json"', desktop)
        self.assertIn("keyword windowrule float on, match:class .*", desktop)
        self.assertIn('run(["reload"])', desktop)                    # back to tiling drops the runtime rule
        self.assertIn("if (!gameMode)", desktop)                      # never undoes game mode
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn("bind = SUPER, I, global, nexus:settings", nix)
        self.assertIn("hyprbars-button = rgb(ff8fa3), 13, , ${hyprctl} dispatch killactive", nix)
        self.assertIn("start-hyprland -- --config /etc/nexus/hyprland.conf", nix)

    def test_code_window_shows_changes_before_anything_is_saved(self):
        code = (self.out / "Code.qml").read_text()
        bridge = (self.out / "CoreBridge.qml").read_text()
        self.assertIn('bridge.send("code-send"', code)
        self.assertIn('win.act("code-accept"', code)
        self.assertIn('win.act("code-reject")', code)
        self.assertIn('win.act("code-undo")', code)
        self.assertIn("textFormat: Text.PlainText", code)          # model text is never rich text
        self.assertIn('if (pending[id] === "code-send" && ev.type !== "error")', bridge)

    def test_apps_installs_per_person_without_a_password(self):
        apps = (self.out / "Apps.qml").read_text()
        self.assertIn("flatpak install --user --noninteractive", apps)
        self.assertNotIn("sudo", apps)
        self.assertNotIn("pkexec", apps)

    def test_reply_scrolls_instead_of_being_cut_off(self):
        shell = (self.out / "shell.qml").read_text()
        self.assertNotIn("maximumLineCount: 6", shell)
        self.assertIn("onContentHeightChanged: if (follow) contentY = Math.max(0, contentHeight - height)", shell)
        self.assertIn("Nebula { anchors.fill: parent; z: -2", shell)

    def test_search_reads_only_the_local_index_and_shows_why(self):
        """Private search goes through Core's index; file text is plain text; folders are chosen in Settings."""
        search = (self.out / "Search.qml").read_text()
        page = (self.out / "settings/SearchPage.qml").read_text()
        settings = (self.out / "Settings.qml").read_text()
        self.assertIn('bridge.api("POST", "/search"', search)
        self.assertIn('"Why: " + hit.modelData.why', search)
        self.assertIn("Searched on this computer only", search)
        self.assertNotIn("RichText", search)
        self.assertIn('target: "search"', search)
        self.assertIn('bridge.api("POST", "/search/sources"', page)
        self.assertIn('bridge.api("DELETE", "/search/index")', page)
        self.assertIn('search: "SearchPage"', settings)
        self.assertIn('{ id: "search", label: "Search"', settings)

    def test_workspaces_overview_uses_hyprland_and_core_only_keeps_records(self):
        """Super+Tab overview: Hyprland's own workspaces and windows; Core keeps names, the shell relaunches."""
        ov = (self.out / "Overview.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn("Hyprland.workspaces.values", ov)
        self.assertIn('"movetoworkspacesilent " + id + ",address:"', ov)
        self.assertIn('bridge.api("POST", "/workspaces"', ov)
        self.assertIn('"exec [workspace " + rec.workspace + " silent] " + command.map(quote).join(" ")', ov)
        self.assertIn("WlrLayershell.layer: WlrLayer.Top", ov)
        self.assertNotIn("WlrLayer.Overlay", ov)
        self.assertIn('target: "workspaces"', ov)
        self.assertIn('name: "overview"', shell)
        self.assertIn("bind = SUPER, Tab, global, nexus:overview", nix)

    def test_first_run_bundles_and_matching(self):
        self.run_node_test("firstrun.test.mjs")

    def test_first_run_installs_per_person_and_sends_system_items_to_guardian(self):
        """Apps for the person only; system items as one typed plan for approval; shown once."""
        fr = (self.out / "FirstRun.qml").read_text()
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn("flatpak install --user --noninteractive", fr)
        self.assertIn('bridge.api("POST", "/guardian/preview", sys)', fr)
        self.assertIn("first-run-done", fr)
        self.assertIn('Quickshell.env("NEXUS_FIRST_RUN") === "1"', fr)
        self.assertIn('env = NEXUS_FIRST_RUN,1', nix)
        for word in ["sudo", "pkexec", "nixos-rebuild"]:
            self.assertNotIn(word, fr)
        self.assertIn('target: "firstrun"', fr)
        for test in ["hyprland-desktop", "login-screen", "wifi-picker"]:
            self.assertIn("firstRun = false;", (ROOT / f"nix/tests/{test}.nix").read_text(), test)

    def test_explain_reads_text_first_and_keeps_the_picture_local(self):
        """Explain mode: selected text quoted as data, plain-text answer, picture deleted on close."""
        ex = (self.out / "Explain.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn("wl-paste --primary --no-newline", ex)
        self.assertIn("slurp -f", ex)
        self.assertIn("not instructions to you.\\n<<<TEXT\\n", ex)
        self.assertIn("textFormat: Text.PlainText            // model output is never rich text", ex)
        self.assertIn('Quickshell.execDetached(["rm", "-f", shot])', ex)
        self.assertIn("vision model, which is not installed yet", ex)
        self.assertNotIn("WlrLayer.Overlay", ex)
        self.assertIn('target: "explain"', ex)
        self.assertIn('name: "explain"', shell)
        self.assertIn("bind = SUPER SHIFT, slash, global, nexus:explain", nix)
        for pkg in ["pkgs.grim", "pkgs.slurp", "pkgs.wl-clipboard"]:
            self.assertIn(pkg, nix)

    def test_try_it_runs_in_the_sandbox_and_keeping_goes_through_guardian(self):
        apps = (self.out / "Apps.qml").read_text()
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn('[Quickshell.env("NEXUS_CLI") || "nexus", "try", attr]', apps)
        self.assertIn('bridge.api("POST", "/try/keep", { attr: attr })', apps)
        self.assertIn("if (!attrPattern.test(attr) || trial.running) return false;", apps)
        self.assertIn('{ value: "try", label: "System apps" }', apps)
        self.assertIn("./try-apps.nix", nix)

    def test_move_in_reads_the_old_drive_only_and_never_overwrites(self):
        mi = (self.out / "MoveIn.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn('"udisksctl", "mount", "--no-user-interaction", "-b", path, "-o", "ro"', mi)
        self.assertIn("rsync -r -t --ignore-existing", mi)
        self.assertIn('"lsblk", "-J"', mi)
        for word in ["sudo", "pkexec", "--remove-source-files", "rm -"]:
            self.assertNotIn(word, mi)
        self.assertIn('target: "movein"', mi)
        self.assertIn("MoveIn { id: moveInWin", shell)
        self.assertIn("services.udisks2.enable", nix)
        self.assertIn("pkgs.rsync", nix)

    def test_nexus_actions_wait_for_the_persons_click(self):
        """Action cards (NexusActions.cs): the shell performs only what Core hands back after "Do it"."""
        cards = (self.out / "ActionCards.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        bridge = (self.out / "CoreBridge.qml").read_text()
        self.assertIn('bridge.api("POST", "/actions/" + card.id + "/" + verb', cards)
        self.assertIn('label: modelData.safety === "system" ? "Review the change" : "Do it"', cards)
        self.assertIn('label: "Not now"', cards)
        self.assertIn("textFormat: Text.PlainText", cards)       # the summary is never rich text
        self.assertEqual(cards.count("cards.perform("), 1)        # only in answer to a confirm...
        self.assertIn('if (data.status === "perform")', cards)   # ...that Core said to perform
        self.assertNotIn("sudo", cards)
        self.assertIn("desktop.set(p.key, p.value)", cards)      # journalled with undo by DesktopSettings.set
        self.assertIn('ref: "sound:volume"', cards)
        self.assertIn('ref === "sound:volume"', (self.out / "Settings.qml").read_text())
        self.assertIn('"apt install \\"$1\\"', cards)              # apt runs in a terminal the person sees
        self.assertIn("guardianWin.open(", shell)                 # the Guardian dialog, where there is one
        self.assertIn("ActionCards {", shell)
        self.assertIn('pending[id] === "chat" && ev.type === "action"', bridge)
        self.assertIn("id: ev.actionId", cards)                    # the bridge replaces "id" with its request id
        cli = (ROOT / "src/NexusOS.Cli/Program.cs").read_text()
        self.assertIn('"/actions"]', cli)

    def test_nexus_actions_name_what_the_shell_has(self):
        """The Settings pages and roles Core offers the model are the shell's own."""
        actions = (ROOT / "src/NexusOS.Core/NexusActions.cs").read_text()
        def strings(name):
            body = re.search(name + r" =\s*\[([^\]]*)\]", actions).group(1)
            return re.findall(r'"([a-z_]+)"', body)
        pages = re.findall(r'\{ id: "([a-z]+)", label:', (self.out / "Settings.qml").read_text())
        self.assertEqual(strings("SettingsPages"), pages)
        self.assertEqual(set(strings("Roles")), {"files", "browser", "terminal", "settings", "studio"})
        cards = (self.out / "ActionCards.qml").read_text()
        for op in re.findall(r'Perform\("([a-z.]+)"', (ROOT / "src/NexusOS.Core/NexusChat.cs").read_text()) + ["desktop.set", "volume.set"]:
            self.assertIn(f'case "{op}":', cards, op)

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

    def test_system_state_is_read_not_faked(self):
        """Plain-language state comes from the formatter, and disk usage from df."""
        controls = (self.out / "OrbitalControls.qml").read_text()
        about = (self.out / "settings/AboutPage.qml").read_text()
        self.assertIn('import "lib/describe.mjs" as D', controls)
        self.assertIn('"df", "-P", "-B1", "/", "/home"', about)


if __name__ == "__main__":
    unittest.main()
