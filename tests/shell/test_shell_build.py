"""Checks for the Nexus shell build (shell/tools/build.py).

Run inside `nix develop .#desktop`: python3 -m unittest tests/shell/test_shell_build.py
"""
import importlib.util
import re
import shutil
import subprocess
import tempfile
import json
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
                     "OrbitalControls.qml", "TopBar.qml", "Settings.qml", "Apps.qml", "Code.qml", "Images.qml", "ActionCards.qml", "Guardian.qml", "greeter.qml", "WifiModel.qml", "DesktopSettings.qml", "Nebula.qml",
                     "Search.qml", "Overview.qml", "FirstRun.qml", "Explain.qml", "MoveIn.qml", "lib/movein.sh", "lib/firstrun.mjs", "lib/bundles.mjs",
                     *[f"settings/{p}Page.qml" for p in ["Network", "Bluetooth", "Sound", "Display", "Power", "Desktop", "Privacy", "About", "Search"]], "settings/AskBox.qml",
                     "lib/orbit.mjs", "lib/describe.mjs", "lib/game.mjs", "lib/wifi.mjs", "lib/tokens.mjs", "lib/nova.frag", "lib/nova-noise-atlas.png"]:
            self.assertTrue((self.out / name).exists(), name)

    def test_scripts_use_only_syntax_the_qml_engine_parses(self):
        # Qt's JavaScript engine has no object spread ({ ...a }): Node runs the
        # module tests happily, but the shell then fails to load at all.
        spread = re.compile(r"\{\s*\.\.\.[\w(]")
        for path in (SHELL / "qml").rglob("*"):
            if path.suffix not in (".mjs", ".js", ".qml"):
                continue
            for n, line in enumerate(path.read_text().splitlines(), 1):
                if line.lstrip().startswith("//"):
                    continue
                self.assertNotRegex(line, spread, f"{path.relative_to(SHELL)}:{n} uses object spread; use Object.assign")

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

    def test_code_runs_tests_only_on_the_persons_run_and_never_sends_a_task_itself(self):
        code = (self.out / "Code.qml").read_text()
        # The agent's test request is a card; the one place a run starts is the Run button.
        self.assertEqual(code.count('bridge.send("code-run"'), 1)
        self.assertEqual(code.count("win.runTests()") + code.count("rc.w.runTests()"), 1)
        self.assertIn('label: "Run"; enabled: !rc.w.working && rc.w.sandbox', code)
        self.assertEqual(code.count("rc.w.runTests()"), 1)
        self.assertIn("if (ev.run) win.proposal = ev.run;", code)
        # A task from Nexus's chat is filled in, not sent (audit: "Code auto-sends the task").
        self.assertIn("prompt.text = win.pendingTask", code)
        self.assertNotIn("win.ask(task)", code)
        self.assertNotIn("win.ask(win.pendingTask", code)
        cli = (ROOT / "src/NexusOS.Cli/Program.cs").read_text()
        self.assertIn('/runs/{Uri.EscapeDataString(Arg("run"))}/confirm', cli)

    def test_code_timeline_is_a_second_view_of_the_same_session(self):
        """Code B: Split or Timeline, remembered per person; one message box, one Run card, the same actions."""
        code = (self.out / "Code.qml").read_text()
        self.assertIn('path: win.configDir + "/code.json"', code)
        self.assertIn('options: [{ value: "split", label: "Split" }, { value: "timeline", label: "Timeline" }]', code)
        self.assertIn("function view(name: string): void { win.setView(name) }", code)
        # Both views place the same components, reading win's properties; nothing is copied.
        self.assertEqual(code.count("RunCard { w: win; width: parent.width }"), 2)
        self.assertEqual(code.count("TestOutput { w: win; width: parent.width }"), 2)
        self.assertEqual(code.count("ViewToggle {"), 2)
        self.assertIn("delegate: DiffLine { required property var modelData; line: modelData", code)
        self.assertIn("parent: win.timeline ? timelineInput : chatPane", code)
        self.assertEqual(code.count("id: prompt"), 1)
        timeline = code.split("id: lineArea")[1]
        self.assertIn("model: win.conversation", timeline)
        self.assertIn("model: win.files", timeline)
        self.assertIn('win.act("code-accept", { files: [card.modelData.path] })', timeline)
        self.assertIn('win.act("code-reject")', timeline)
        # Still one place a test run starts, and one request to Core for it.
        self.assertEqual(code.count('bridge.send("code-run"'), 1)

    def test_images_orbit_shows_the_gallerys_pictures_with_the_same_actions(self):
        """Images B: Gallery or Orbit, remembered per person; both show win.shown and call the same functions."""
        images = (self.out / "Images.qml").read_text()
        self.assertIn('path: win.configDir + "/images.json"', images)
        self.assertIn('options: [{ value: "gallery", label: "Gallery" }, { value: "orbit", label: "Orbit" }]', images)
        self.assertIn("function view(name: string): void { win.setView(name) }", images)
        gallery, orbit = images.split("id: results")[1].split("id: orbitView")
        self.assertIn("readonly property var shown: win.shown", gallery)
        self.assertIn("model: win.shown", orbit)
        for fn in ["win.openImage(win.selected)", "win.moreLike(win.selected)", "win.again(win.selected)",
                   "win.remove(win.selected)", "win.showFolder(win.selected)", "win.remix(win.selected, modelData.words)",
                   "win.useAsWallpaper(win.selected.path)"]:
            self.assertIn(fn, gallery, fn)
            self.assertIn(fn, orbit, fn)
        self.assertEqual(images.count('bridge.send("images-delete"'), 1)
        self.assertEqual(images.count('bridge.send("images-generate"'), 1)

    def test_settings_ask_uses_nexus_chat_and_action_cards(self):
        """Settings C: the ask box goes through Core's chat and /actions (ActionCards), never its own model call."""
        ask = (self.out / "settings/AskBox.qml").read_text()
        settings = (self.out / "Settings.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        live = (self.out / "live.qml").read_text()
        self.assertIn('chatId = bridge.chat("From Settings: " + q)', ask)
        self.assertEqual(ask.count("bridge.send("), 0)
        self.assertEqual(ask.count("bridge.api("), 0)
        self.assertIn("ActionCards {", ask)
        self.assertIn("const m = host.search(q);", ask)           # Settings' own index lights the page at once
        self.assertIn('if (ev.action === "open_settings" && a.page)', ask)
        self.assertIn('ev.action === "change_setting"', ask)
        self.assertIn("if (!bridge) return;", ask)                 # the live installer has no Nexus
        self.assertIn("textFormat: Text.PlainText", ask)
        self.assertIn("AskBox {", settings)
        self.assertIn("askBox.show(text.trim())", settings)
        self.assertIn("function ask(text: string): void", settings)
        self.assertIn("cardHost: actionCards", shell)
        self.assertNotIn("cardHost", live)

    def test_apps_installs_per_person_without_a_password(self):
        apps = (self.out / "Apps.qml").read_text()
        self.assertIn("flatpak install --user --noninteractive", apps)
        self.assertNotIn("sudo", apps)
        self.assertNotIn("pkexec", apps)

    def test_apps_says_what_an_app_can_do_before_installing_and_journals_an_undo(self):
        """Install sheet from Core's details; changes reported to the journal; leftover data only on request."""
        apps = (self.out / "Apps.qml").read_text()
        cards = (self.out / "ActionCards.qml").read_text()
        timeline = (self.out / "settings/TimelinePage.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        # Install opens the sheet; only the sheet's button installs.
        self.assertIn("onClicked: win.requestInstall(modelData.id)", apps)
        self.assertEqual(apps.count("onClicked: win.install("), 1)
        self.assertIn('bridge.api("GET", "/apps/flathub/" + id)', apps)
        self.assertIn("win.sheet.warning", apps)
        # Each change is reported with what Undo needs, read from flatpak info.
        self.assertIn('bridge.api("POST", "/apps/changes", body)', apps)
        self.assertIn("LC_ALL=C flatpak info", apps)
        self.assertIn("--commit=", apps)
        # Leftover data: offered after a removal, deleted only from its own button.
        self.assertIn('label: "Delete its data"; onClicked: win.deleteLeftover()', apps)
        self.assertEqual(apps.count("win.deleteLeftover()"), 1)
        self.assertIn('"rm -rf -- \\"$HOME/.var/app/$1\\""', apps)
        # apt is said plainly, with the isolated container on offer.
        self.assertIn("Ubuntu software runs as you and can read your home folder", apps)
        self.assertIn("apt --isolated", apps)
        # Nexus's cards and the Timeline's Undo go through Apps.perform.
        self.assertIn("appsWin.perform(p)", cards)
        self.assertIn("modelData.details.warning", cards)
        self.assertIn("page.host.appChange(data.perform)", timeline)
        self.assertIn("onAppChange: perform => appsWin.perform(perform)", shell)
        apt = (ROOT / "nix/packages/nexus-apt.sh").read_text()
        self.assertIn("is NOT a sandbox", apt)
        self.assertIn("nexus \"${args[@]}\"", apt)

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

    def test_workspaces_restore_files_tabs_and_conversation_from_cores_plan(self):
        """Restore asks Core what can come back now, hands files to %F/%f, and says what it could not."""
        ov = (self.out / "Overview.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        self.assertIn('bridge.api("GET", "/workspaces/restore?name="', ov)
        self.assertIn('a === "%F" || a === "%U"', ov)
        self.assertIn("conversation: conversation || null", ov)
        self.assertIn("conversationRestored(plan.conversation.prompt, plan.conversation.answer)", ov)
        self.assertNotIn("not kept yet", ov)
        self.assertIn("onConversationRestored:", shell)

    def test_settings_orbit_groups_every_page_once(self):
        """The orbit shows four groups; each page is in exactly one, and deep links still name pages."""
        settings = (self.out / "Settings.qml").read_text()
        pages = re.findall(r'\{ id: "([a-z]+)", label:', settings)
        members = re.findall(r'\{ key: "[a-z]+", label: "[^"]+", colour: "[^"]+", icon: "([a-z]+)", members: \[([^\]]*)\] \}', settings)
        self.assertEqual(len(members), 4)
        grouped = [m for _, body in members for m in re.findall(r'"([a-z]+)"', body)]
        self.assertEqual(sorted(grouped), sorted(pages))
        for icon, _ in members:
            self.assertTrue((self.out / "icons" / f"{icon}.svg").exists(), icon)
        self.assertIn("readonly property string expandedGroup: groupOf(page).key", settings)
        self.assertIn("Keys.onRightPressed: stepFocus(1)", settings)
        # The live installer's `only` filters the groups too.
        self.assertIn("pages.some(p => p.id === m)", settings)

    def test_greeter_changes_an_expired_password_and_has_a_watchdog(self):
        """Expired: current, new, new again (10 or more), then the login carries on; a hung greeter gives way."""
        greeter = (self.out / "greeter.qml").read_text()
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        patch = (ROOT / "nix/patches/greetd-expired-password.patch").read_text()
        self.assertIn("Your password has expired", greeter)
        self.assertIn("readonly property int minLength: 10", greeter)
        self.assertIn("NEW_AUTHTOK_REQD", greeter)
        self.assertNotIn("console.info(\"nexus-greeter: \" + root.held", greeter)    # answers are never logged
        self.assertIn("heartbeat.setText(", greeter)
        self.assertIn("pam.chauthtok(PamFlag::CHANGE_EXPIRED_AUTHTOK)?", patch)
        self.assertIn("../patches/greetd-expired-password.patch", nix)
        self.assertIn("package = greetd;", nix)
        self.assertIn('export NEXUS_GREETER_HEARTBEAT="$runtime/nexus-greeter.alive"', nix)
        self.assertIn("nexus-greeter stopped answering; using the text login", nix)

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
        # Privacy: no screenshot at all; the selection is read only after a pick, in its own step.
        self.assertNotIn("grim", ex)
        self.assertNotIn("wl-paste", ex[ex.index("pickScript:"):ex.index("property var pickedArea")])
        self.assertIn('if (code !== 0) { card.state_ = "idle"; return }      // Esc in slurp: nothing chosen, nothing read', ex)
        # A read-only answer, saved to history only when the person chooses.
        self.assertIn("chatId = bridge.explain(text)", ex)
        self.assertNotIn("bridge.chat(", ex)
        self.assertIn('bridge.api("POST", "/explain/keep"', ex)
        self.assertIn('function explain(prompt) { return send("explain"', (self.out / "CoreBridge.qml").read_text())
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
        # Always read-only (ext2/3/4 with noload too); a drive inside the computer asks for a password through polkit.
        self.assertIn('mountOptions = /^ext[234]$/.test(d.fstype) ? "ro,noload" : "ro";', mi)
        self.assertIn('const opts = mountOptions + (apfsVol >= 0 ? ",vol=" + apfsVol : "");', mi)
        self.assertIn('["udisksctl", "mount"].concat(withPassword ? [] : ["--no-user-interaction"]).concat(["-b", source, "-o", opts])', mi)
        self.assertEqual(len(re.findall(r'mountOptions = ', mi)), 2)
        # Inside an unlocked BitLocker or LUKS drive: the same rule.
        self.assertIn('win.mountOptions = /^ext[234]$/.test(fs) ? "ro,noload" : "ro";', mi)
        self.assertIn('unlocker.command = ["sh", helper, "unlock", source];', mi)
        self.assertIn('onStarted: { write(key + "\\n"); key = ""; stdinEnabled = false }', mi)
        self.assertNotIn('"rw', mi)
        self.assertIn("if (!d.removable) {", mi)
        self.assertIn('d.fstype === "bitlocker"', mi)
        # APFS read-only: udisks may never pass the module's "readwrite" option.
        self.assertIn("boot.supportedFilesystems.apfs", nix)
        allow = re.search(r"apfs_allow=(\S+)", nix).group(1).split(",")
        self.assertNotIn("readwrite", allow)
        self.assertIn("ro", re.search(r"apfs_defaults=(\S+)", nix).group(1).split(","))
        self.assertIn('"lsblk", "-J"', mi)
        # Bookmarks and apps through Core; apps only listed in Apps.
        self.assertIn('bridge.api("POST", "/movein/look"', mi)
        self.assertIn("onGetApps: list => appsWin.suggest(list)", shell)
        cards = (self.out / "ActionCards.qml").read_text()
        self.assertIn('case "apps.suggest": appsWin.suggest(p.apps || [])', cards)
        self.assertIn('case "movein.copy":', cards)
        helper = (self.out / "lib/movein.sh").read_text()
        self.assertIn("rsync -r -t --ignore-existing", helper)
        for text in [mi, helper]:
            for word in ["sudo", "pkexec", "--remove-source-files", "rm -", "--delete"]:
                self.assertNotIn(word, text)
        self.assertIn('target: "movein"', mi)
        self.assertIn("MoveIn { id: moveInWin", shell)
        self.assertIn("services.udisks2.enable", nix)
        self.assertIn("pkgs.rsync", nix)

    def test_move_in_does_not_trust_the_old_drive(self):
        """Names on the old drive never pick a destination; the helper (tested in test_movein.py) copies."""
        mi = (self.out / "MoveIn.qml").read_text()
        helper = (self.out / "lib/movein.sh").read_text()
        self.assertIn('Quickshell.shellPath("lib/movein.sh")', mi)
        self.assertIn('scanner.command = ["sh", helper, "scan", root];', mi)
        self.assertIn('rsync.command = ["sh", helper, "copy", root, copying.user, copying.folder,', mi)
        # NUL-separated records, never lines, and no path from the drive comes back to the shell.
        self.assertIn('text.split("\\0")', mi)
        self.assertNotIn('text.split("\\n")', re.search(r"id: scanner[\s\S]*?\n    \}", mi).group(0))
        self.assertNotIn("path: f[", mi)
        self.assertNotIn('"sh", "-c"', mi)
        self.assertIn("!win.validUser(f[1])", mi)
        for flag in ["--no-links", "--no-devices", "--no-specials", "--no-perms", "--no-owner", "--no-group", "--ignore-existing"]:
            self.assertIn(flag, helper)
        rsync = helper.split("exec rsync", 1)[1].split('"$dest/"', 1)[0].split()
        for flag in ["-a", "--archive", "-l", "--links", "-L", "--copy-links", "-K", "--keep-dirlinks", "-p", "-o", "-g", "-D"]:
            self.assertNotIn(flag, rsync)
        self.assertIn("Documents|Desktop|Downloads|Pictures|Music|Videos) printf '%s' \"$1\"", helper)
        self.assertIn("''|.*|*/*|*[[:cntrl:]]*) return 1", helper)
        self.assertIn('die "refusing: $1 is a symlink"', helper)
        self.assertIn("printf 'item\\t%s\\t%s\\t%s\\0'", helper)

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
        # apt runs in a terminal the person sees (Apps.aptRun, which cards reach through appsWin.perform).
        self.assertIn('then apt --isolated \\"$1\\" \\"$2\\"; else apt \\"$1\\" \\"$2\\"; fi', (self.out / "Apps.qml").read_text())
        self.assertIn("guardianWin.open(", shell)                 # the Guardian dialog, where there is one
        self.assertIn("ActionCards {", shell)
        self.assertIn('pending[id] === "chat" && ev.type === "action"', bridge)
        self.assertIn("id: ev.actionId", cards)                    # the bridge replaces "id" with its request id
        cli = (ROOT / "src/NexusOS.Cli/Program.cs").read_text()
        self.assertIn('"/actions"]', cli)

    def test_action_cards_show_the_whole_text_and_follow_up(self):
        """Cards show an action's full text (scrolling, says if cut); after Do it Nexus says what happened."""
        cards = (self.out / "ActionCards.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        bridge = (self.out / "CoreBridge.qml").read_text()
        cli = (ROOT / "src/NexusOS.Cli/Program.cs").read_text()
        self.assertIn("text: modelData.detail", cards)
        self.assertIn("height: Math.min(detailText.implicitHeight, 160)", cards)
        self.assertIn('"Shortened here: showing the first "', cards)
        self.assertNotIn("Clean(", cards)
        self.assertIn("cards.finished(cardId, ok, message)", cards)
        self.assertIn("bridge.followUp(cardId, ok, message)", shell)
        self.assertIn('send("followup"', bridge)
        self.assertIn('"followup" => $"/actions/{Uri.EscapeDataString(Arg("action"))}/followup"', cli)
        # Desktop actions confirmed in a terminal are collected from Core and done here, once.
        self.assertIn('"/actions/desktop/collect"', cards)
        self.assertIn("const said = perform(item.perform)", cards)

    def test_every_desktop_instruction_core_sends_is_handled(self):
        """Each Perform("op") in NexusChat.cs has a case in ActionCards.perform, so no "Do it" falls through."""
        chat = (ROOT / "src/NexusOS.Core/NexusChat.cs").read_text()
        cards = (self.out / "ActionCards.qml").read_text()
        ops = set(re.findall(r'Perform\("([a-z.]+)"', chat))
        self.assertGreaterEqual(len(ops), 10)
        for op in ops:
            self.assertIn(f'case "{op}":', cards, op)

    def test_guardian_dialog_approves_only_with_the_persons_password(self):
        """Guardian.qml: approval is polkit's password prompt, then the broker's review and staging."""
        guardian = (self.out / "Guardian.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        bar = (self.out / "TopBar.qml").read_text()
        # The person's click starts the checks; approval is the polkit helper after their password.
        self.assertIn('"/guardian/proposals/" + p.id + "/check"', guardian)
        self.assertIn("approver.command = [pkexec, approveHelper].concat(GD.helperArgs(r))", guardian)
        self.assertEqual(guardian.count("approver.running = true"), 1)
        self.assertIn("/run/current-system/sw/bin/nexus-guardian-desktop-approve", guardian)
        # Only after polkit succeeds: the existing review with the builder's attestation, then staging.
        self.assertIn('win.call("review", "POST", "/guardian/review", { candidateId: p.candidateId, attestation: p.attestation }', guardian)
        self.assertIn('if (data.eligible === true && data.decision)', guardian)
        self.assertIn('"/guardian/stage", { decision: data.decision }', guardian)
        # The password goes only to polkit, is never kept, and shows as dots.
        self.assertIn("win.flow.submit(password.text); password.text = \"\"", guardian)
        self.assertIn("TextInput.Password", guardian)
        self.assertNotIn("sudo", guardian)
        self.assertNotIn("localStorage", guardian)
        self.assertIn("textFormat: Text.PlainText", guardian)     # proposal text is never rich text
        # Everything the person must see.
        for text in ("Approve and stage", "Decline", "Show the full change", "What changes", "Restart now",
                     "Only allowed settings and packages are touched", "Built on this computer",
                     "Starts up properly in a test machine", "goes back to today's version"):
            self.assertIn(text, guardian, text)
        # The shell talks to Core only through the bridge; IPC and the action cards' entry point exist.
        self.assertNotIn("nexus.sock", guardian)
        self.assertIn('target: "guardian"', guardian)
        self.assertIn("function open(id, plan)", guardian)
        self.assertIn("Guardian { id: guardianWin; theme: root.dark; bridge: bridge }", shell)
        self.assertIn("guardian: guardianWin", shell)
        self.assertIn('onProposeSystem: plan => guardianWin.openPlan(plan, "user")', shell)   # First run's system choices
        self.assertIn("bar.guardian.open()", bar)
        cli = (ROOT / "src/NexusOS.Cli/Program.cs").read_text()
        for prefix in ("/guardian/proposals", "/guardian/review", "/guardian/stage"):
            self.assertIn(f'"{prefix}"', cli)
        # The polkit action's helper path is the one the dialog runs, and asks every time.
        module = (ROOT / "nix/modules/guardian.nix").read_text()
        self.assertIn('approveHelper = "/run/current-system/sw/bin/nexus-guardian-desktop-approve"', module)
        self.assertIn("<allow_active>auth_admin</allow_active>", module)

    def test_security_updates_are_shown_and_approved_through_the_guardian_dialog(self):
        """Updates: the top bar says they are ready; the dialog shows the broker's signed versions and lock."""
        guardian = (self.out / "Guardian.qml").read_text()
        bar = (self.out / "TopBar.qml").read_text()
        self.assertIn('g.updatesReady ? "Security updates are ready"', bar)
        self.assertIn("updatesReady = !!list.updatesReady", guardian)
        self.assertIn('readonly property var shownUpdate: held && held.kind === "update" ? held.update', guardian)
        self.assertIn("GD.updateLine(win.shownUpdate)", guardian)
        self.assertIn("GD.revisions(win.shownUpdate)", guardian)
        self.assertIn('p.kind === "update" ? "SECURITY UPDATES FROM NIXOS"', guardian)
        # Approved the same way as any change: the password for the broker's candidate and build.
        self.assertEqual(guardian.count("approver.running = true"), 1)
        self.assertNotIn("/guardian/updates", guardian)   # the dialog never starts or skips checks itself
        # The check service is a root unit with a timer, started on request through the builder, never sudo.
        updates = (ROOT / "nix/modules/updates.nix").read_text()
        for text in ('OnCalendar = "weekly"', "Persistent = true", "RandomizedDelaySec", "Nice = 19", 'IOSchedulingClass = "idle"',
                     "PathChanged = request", "--min-free-gib", "network-online.target"):
            self.assertIn(text, updates)
        self.assertNotIn("sudo", updates)
        self.assertIn("../modules/updates.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())

    def test_guardian_approval_is_bound_to_the_brokers_record(self):
        """What the person approves is the broker's record, named in polkit's prompt, and only that
        request gets the dialog's password box."""
        guardian = (self.out / "Guardian.qml").read_text()
        self.assertIn('import "lib/guardian.mjs" as GD', guardian)
        # Shown: the broker's describe, not Core's proposal text; Core's title only as "Nexus says".
        self.assertIn('"/guardian/proposals/" + p.id + "/describe"', guardian)
        self.assertIn("readonly property var shownItems: held ? GD.items(held) : current ? current.items : []", guardian)
        self.assertIn("model: win.shownItems", guardian)
        self.assertNotIn("model: win.current ? win.current.items", guardian)
        self.assertIn("text: win.held ? GD.title(win.held) : win.current ? win.current.title", guardian)
        self.assertIn('"Nexus says: “" + win.current.title', guardian)
        self.assertIn("readonly property bool disagrees: !!held && !GD.agrees(current.items, held, current.lockHash)", guardian)
        self.assertIn("visible: win.disagrees", guardian)
        # The password is asked for only for the record on screen, and only when Nexus agrees unless the person clicks.
        self.assertIn("function askPassword(p, r)", guardian)
        self.assertIn("if (approver.running || !GD.isRecord(r) || r.candidateId !== p.candidateId) return;", guardian)
        self.assertIn('current.state === "ready" && held && !disagrees) askPassword(current, held)', guardian)
        self.assertEqual(guardian.count("askPassword("), 4)   # the definition, Approve, and the two automatic continuations
        # The agent: the dialog's password box only for Guardian's action naming this candidate and build.
        self.assertIn("readonly property bool ownFlow: !!flow && approving && !!approver.record\n"
                      "                                    && GD.ownsFlow(flow.actionId, flow.message, approver.record)", guardian)
        self.assertIn("visible: win.ownFlow; width: parent.width", guardian)
        self.assertNotIn('flow.actionId === "org.nexusos.guardian.approve" && approving', guardian)
        # Everything else: the plain window, with the action and message as polkit sent them.
        other = guardian[guardian.index("id: other"):]
        self.assertIn("visible: !!win.flow && !win.ownFlow\n", other)
        self.assertIn("text: win.flow ? win.flow.message : \"\"", other)
        self.assertIn('"Requested action: " + win.flow.actionId', other)
        self.assertIn("win.flow.actionId === GD.APPROVE_ACTION", other)
        lib = (self.out / "lib/guardian.mjs").read_text()
        self.assertIn('export const APPROVE_ACTION = "org.nexusos.guardian.approve";', lib)
        self.run_node_test("guardian.test.mjs")
        # Polkit's prompt names the helper's command line (candidate ID and build), and the helper takes both.
        module = (ROOT / "nix/modules/guardian.nix").read_text()
        policy = module[module.index('<action id="org.nexusos.guardian.approve">'):module.index("</action>")]
        self.assertIn("$(command_line)", policy)
        helper = (ROOT / "scripts/guardian/desktop_approve.py").read_text()
        self.assertIn("nexus-guardian-desktop-approve <candidateId> <buildHash>", helper)
        self.assertIn("broker.register_approval(candidate_id, uid, build=build)", helper)

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

    def test_describe_this_and_voice_stay_local_and_listen_only_on_request(self):
        """Vision.cs/Voice.cs with Describe.qml/Voice.qml: pictures and the microphone only when asked, all local."""
        voice = (self.out / "Voice.qml").read_text()
        describe = (self.out / "Describe.qml").read_text()
        shell = (self.out / "shell.qml").read_text()
        page = (self.out / "settings/AccessibilityPage.qml").read_text()
        explain = (self.out / "Explain.qml").read_text()
        cards = (self.out / "ActionCards.qml").read_text()
        nix = (ROOT / "nix/modules/desktop.nix").read_text()
        cli = (ROOT / "src/NexusOS.Cli/Program.cs").read_text()
        # Push-to-talk: Super+V held (press/release), the recording made only in press(), a visible indicator.
        self.assertIn("bind = SUPER, V, global, nexus:talk", nix)
        self.assertIn('name: "talk"; onPressed: voiceCtl.press(); onReleased: voiceCtl.release()', shell)
        self.assertEqual(voice.count("recorder.running = true"), 1)
        self.assertIn('visible: voice.state_ !== "idle"', voice)                # the bar shows whenever the mic is open
        self.assertIn('WlrLayershell.namespace: "nexus-microphone"', voice)
        self.assertIn("interval: 30000", voice)                                 # a recording stops by itself
        self.assertIn('bridge.api("POST", "/voice/transcribe", { audio: "voice.wav" })', voice)
        self.assertIn('onHeard: text => { root.open(); input.text = text; root.ask(text) }', shell)
        self.assertIn('icons/mic.svg', shell)
        self.assertIn("voiceCtl.speak(root.reply)", shell)
        self.assertIn("if (!spokenReplies", voice)                               # spoken replies only when turned on
        # Describe this: grim only after the person's request, then Core (local vision model only).
        self.assertIn('bridge.api("POST", "/vision/describe"', describe)
        self.assertNotIn("cloud", describe.lower().replace("never goes to a cloud model", ""))
        self.assertEqual(describe.count("capture.running = true"), 1)
        self.assertIn('case "describe.screen":', cards)
        self.assertIn("describer.describeRegion(", explain)
        self.assertIn("---- describe the picture (Describe.qml) ----", explain)
        # Settings, Accessibility: real switches; the old placeholder is gone.
        self.assertNotIn("Coming later", page)
        for label in ['label: "Describe this"', 'label: "Voice control"', 'label: "Spoken replies"']:
            self.assertIn(label, page)
        self.assertIn('page.voice.setChoice("pushToTalk"', page)
        self.assertIn('page.voice.setChoice("spokenReplies"', page)
        self.assertIn('"/vision", "/voice"', cli)
        guide = (ROOT / "src/NexusOS.Core/assistant/nexus-guide.md").read_text()
        self.assertIn("Super+V", guide)
        self.assertIn("describe_screen", guide)

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

    def test_app_windows_open_again_after_being_closed(self):
        """Every app window is a NexusWindow, which marks itself hidden when Hyprland closes it."""
        window = (self.out / "NexusWindow.qml").read_text()
        self.assertIn("onClosed: visible = false", window)
        for qml in sorted(self.out.glob("*.qml")):
            if qml.name != "NexusWindow.qml":
                self.assertNotRegex(qml.read_text(), r"(?m)^FloatingWindow \{", qml.name)

    def test_live_session_offers_only_wifi_and_install(self):
        live = (self.out / "live.qml").read_text()
        self.assertIn('only: ["network"]', live)
        self.assertIn("nexus-install-welcome", live)
        self.assertIn("live: true", live)
        for absent in ("CoreBridge", "FirstRun", "Apps {", "Code {", "Guardian {", "NovaCore"):
            self.assertNotIn(absent, live)
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn('if cfg.live then "nexus-live-shell" else "nexus-shell"', desktop)
        self.assertIn("lib.optionalString (!cfg.live) appBinds", desktop)
        iso = (ROOT / "nix/installer/iso.nix").read_text()
        self.assertIn("live = true;", iso)
        self.assertIn("services.nexus-core.enable = false;", iso)

    def test_settings_offers_the_large_model_and_says_what_stops_it(self):
        page = (self.out / "settings/PrivacyPage.qml").read_text()
        self.assertIn('bridge.api("GET", "/model/large")', page)
        self.assertIn('bridge.api("POST", "/model/large/use", { on: !page.largeOn })', page)
        self.assertIn("The standard model answers meanwhile.", page)      # blocked: short of memory, failed to start
        self.assertIn("d.note", page)                                     # which version was chosen, and why
        self.assertIn('"largemodel", "Large model"', (self.out / "Settings.qml").read_text())

    def test_settings_downloads_the_very_large_model_only_when_asked_and_can_remove_it(self):
        page = (self.out / "settings/PrivacyPage.qml").read_text()
        self.assertIn('bridge.api("GET", "/model/colibri")', page)
        # Only the Download button asks for it; nothing else posts on: true to /want.
        self.assertEqual(page.count('"/model/colibri/want",\n'), 1)
        self.assertIn('{ on: true, model: page.colibriPick || page.colibri.model }', page)
        self.assertIn('"/model/colibri/want", { on: false }', page)
        self.assertIn('bridge.api("POST", "/model/colibri/use", { on: !page.colibri.selected })', page)
        # Removing deletes many GB: it asks first.
        self.assertIn("confirmRemove", page)
        self.assertIn('bridge.api("DELETE", "/model/colibri")', page)
        # Only models this computer can hold are offered.
        self.assertIn("page.colibriModels.filter(m => m.fits)", page)
        self.assertIn("AVX2", page)
        self.assertIn('"verylargemodel", "Very large model"', (self.out / "Settings.qml").read_text())


    def test_apps_notifications_pop_up_and_wait_in_the_centre_as_plain_text(self):
        qml = (self.out / "Notifications.qml").read_text()
        self.assertIn("NotificationServer {", qml)
        # An app cannot put markup or links in front of the person.
        self.assertIn("bodyMarkupSupported: false", qml)
        self.assertIn("bodyHyperlinksSupported: false", qml)
        self.assertIn("n.tracked = true", qml)                        # kept for the centre until dismissed
        self.assertIn("N.showCard(n.urgency === NotificationUrgency.Critical, notifications.doNotDisturb", qml)
        card = (self.out / "NotificationCard.qml").read_text()
        self.assertEqual(card.count("textFormat: Text.PlainText"), 3)
        self.assertIn("Notifications { id: notifications;", (self.out / "shell.qml").read_text())
        bar = (self.out / "TopBar.qml").read_text()
        self.assertIn('source: "icons/bell.svg"', bar)
        self.assertIn("bar.notifications.centreOpen = !bar.notifications.centreOpen", bar)
        self.assertIn("property bool doNotDisturb: false", (self.out / "DesktopSettings.qml").read_text())
        self.assertIn('page.desktop.set("doNotDisturb"', (self.out / "settings/DesktopPage.qml").read_text())
        self.run_node_test("notifications.test.mjs")

    def test_accounts_change_the_password_with_the_disk_and_never_put_it_on_a_command_line(self):
        page = (self.out / "settings/AccountsPage.qml").read_text()
        # Through pkexec, the setuid wrapper, and the paths the polkit policy names.
        self.assertIn('"/run/wrappers/bin/pkexec", "/run/current-system/sw/bin/" + helper, operation]', page)
        self.assertIn("onStarted: { write(request + \"\\n\"); stdinEnabled = false }", page)
        for line in page.splitlines():
            if "command = " in line or "command: [" in line:
                self.assertNotIn("password", line.replace("nexus-account-password", "").replace("nexus-keyring-password", ""))
        self.assertEqual(page.count("password: true"), 9)      # 7 account fields, the TPM disk password and the Nextcloud app password
        settings = (self.out / "Settings.qml").read_text()
        self.assertIn('accounts: "AccountsPage"', settings)
        policy = (ROOT / "nix/modules/accounts.nix").read_text()
        self.assertIn("<allow_active>auth_self</allow_active>", policy)
        self.assertIn("<allow_active>auth_admin</allow_active>", policy)
        self.assertIn("/run/current-system/sw/bin/nexus-account-password</annotate>", policy)
        self.run_node_test("accounts.test.mjs")

    def test_the_screen_locks_before_every_sleep_and_sleep_follows_settings(self):
        idle = (self.out / "Idle.qml").read_text()
        # A delay inhibitor holds sleep back until the lock screen is up, then is let go.
        self.assertIn('"systemd-inhibit", "--what=sleep", "--mode=delay"', idle)
        self.assertIn('"gdbus", "monitor", "--system", "--dest", "org.freedesktop.login1"', idle)
        self.assertIn("idle.lock();\n                    release.restart();", idle)
        self.assertEqual(idle.count("respectInhibitors: true"), 2)     # videos and calls keep the screen on
        self.assertIn('"--what=handle-lid-switch", "--mode=block"', idle)
        self.assertIn("Idle { desktop: desktop }", (self.out / "shell.qml").read_text())
        power = (self.out / "settings/PowerPage.qml").read_text()
        for key in ("screenOffMinutes", "sleepBatteryMinutes", "sleepPluggedMinutes", "lidAction"):
            self.assertIn('page.desktop.set("%s"' % key, power)
        self.run_node_test("power.test.mjs")

    def test_printers_appear_by_themselves_and_names_are_checked(self):
        page = (self.out / "settings/PrintersPage.qml").read_text()
        self.assertIn('command: ["lpstat", "-e"]', page)
        self.assertIn('setDefault.command = ["lpoptions", "-d", modelData]', page)
        self.assertEqual(page.count("P.validName("), 2)
        self.assertIn('print.command = ["lp", "-d", name, "-t", "NexusOS test page"]', page)
        module = (ROOT / "nix/modules/printing.nix").read_text()
        for line in ("services.ipp-usb.enable", "nssmdns4", "pkgs.sane-airscan", "pkgs.simple-scan"):
            self.assertIn(line, module)
        self.assertIn("../modules/printing.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())
        self.run_node_test("printers.test.mjs")

    def test_backups_run_as_the_person_and_restore_beside_the_files(self):
        page = (self.out / "settings/BackupsPage.qml").read_text()
        self.assertIn('command: ["nexus-backup", "status"]', page)
        self.assertIn('worker.command = ["nexus-backup", operation].concat(args || [])', page)
        self.assertNotIn("pkexec", page)                       # never as root
        self.assertIn('page.work("setup", ["--dest", page.chosenDrive], password.text', page)   # password on stdin
        self.assertIn("B.restorePath(page.home, modelData)", page)
        module = (ROOT / "nix/modules/backups.nix").read_text()
        for line in ("systemd.user.timers.nexus-backup", "nexus-backup auto", "pkgs.restic"):
            self.assertIn(line, module)
        self.assertIn("../modules/backups.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())
        self.run_node_test("backups.test.mjs")

    def test_firmware_updates_go_through_fwupd_with_checked_ids(self):
        page = (self.out / "settings/UpdatesPage.qml").read_text()
        self.assertIn('command: ["fwupdmgr", "get-updates", "--json"]', page)
        self.assertIn('updater.command = ["fwupdmgr", "update", "--assume-yes", "--no-reboot-check"].concat(ok.map(u => u.id))', page)
        self.assertIn("/^[0-9a-f]{40}$/.test(u.id)", page)
        module = (ROOT / "nix/modules/firmware.nix").read_text()
        self.assertIn("services.fwupd.enable", module)
        self.assertIn("../modules/firmware.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())
        self.run_node_test("firmware.test.mjs")
        self.assertIn('"/run/current-system/sw/bin/nexus-update-channel", c]', page)
        self.assertIn('page.bridge.api("POST", "/guardian/updates/check", {})', page)
        updates = (ROOT / "nix/modules/updates.nix").read_text()
        self.assertIn("stable|testing) ;;", updates)
        self.assertIn("org.nexusos.updates.channel", updates)

    def test_language_and_region_are_per_person_and_checked(self):
        page = (self.out / "settings/RegionPage.qml").read_text()
        self.assertIn('regionFile.setText(R.envFile(next))', page)
        self.assertIn('command: ["timedatectl", "list-timezones"]', page)
        self.assertNotIn("pkexec", page)
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn("LANG|LANGUAGE|LC_TIME|LC_NUMERIC|LC_MONETARY|LC_PAPER|LC_MEASUREMENT|TZ", desktop)
        self.assertIn('*[!A-Za-z0-9_.@:/+-]*) continue', desktop)
        settings = (self.out / "DesktopSettings.qml").read_text()
        self.assertIn('"keyword input:kb_layout " + kb.layout', settings)
        # Every catalogue translates only texts the shell has, and is valid JSON.
        sources = "".join(p.read_text() for p in self.out.rglob("*.qml"))
        self.assertEqual(sorted(c.stem for c in (self.out / "i18n").glob("*.json")), ["de", "es", "fr"])
        for catalogue in (self.out / "i18n").glob("*.json"):
            for text in json.loads(catalogue.read_text()):
                self.assertTrue('"%s"' % text in sources, "%s translates %r, which the shell does not have" % (catalogue.name, text))
        self.run_node_test("region.test.mjs")

    def test_screens_are_arranged_with_a_way_back(self):
        page = (self.out / "settings/DisplayPage.qml").read_text()
        self.assertIn("countdown = 15", page)
        self.assertIn("page.revert()", page)
        settings = (self.out / "DesktopSettings.qml").read_text()
        self.assertIn('command: [desktop.hyprctl, "monitors", "all", "-j"]', settings)
        self.assertIn("function onScreensChanged()", settings)
        self.run_node_test("displays.test.mjs")

    def test_storage_and_default_apps(self):
        page = (self.out / "settings/StoragePage.qml").read_text()
        self.assertIn('"/run/current-system/sw/bin/nexus-storage-clean"', page)
        self.assertIn('command: ["nexus-storage", "report"]', page)
        apps = (self.out / "settings/DefaultAppsPage.qml").read_text()
        self.assertIn('setter.command = ["nexus-default-apps", "set", category, app]', apps)
        module = (ROOT / "nix/modules/storage.nix").read_text()
        self.assertIn("org.nexusos.storage.clean", module)
        self.assertIn("report|empty-trash|clear-cache)", module)      # the person's command never cleans the system
        self.assertIn("../modules/storage.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())
        self.run_node_test("storage.test.mjs")

    def test_secure_boot_goes_through_guardian_then_one_root_step(self):
        page = (self.out / "settings/AboutPage.qml").read_text()
        self.assertIn("page.host.proposeSystem(SB.PLAN)", page)
        self.assertIn('"/run/current-system/sw/bin/nexus-secure-boot-enroll"', page)
        self.assertIn("onProposeSystem: plan => guardianWin.openPlan(plan, \"user\")", (self.out / "shell.qml").read_text())
        schema = json.loads((ROOT / "schema/guardian-options.json").read_text())
        self.assertEqual(schema["options"]["nexusos.secureBoot.enable"]["risk"], "elevated")
        module = (ROOT / "nix/modules/secure-boot.nix").read_text()
        self.assertIn("boot.loader.systemd-boot.editor = lib.mkForce false", module)
        self.assertIn('"enroll-keys", "--microsoft", "--firmware-builtin"', (ROOT / "scripts/boot/nexus_secure_boot.py").read_text())
        self.run_node_test("secureboot.test.mjs")

    def test_keys_show_how_much_and_windows_shortcuts_are_bound(self):
        osd = (self.out / "OnScreen.qml").read_text()
        for name in ("volup", "voldown", "mute", "micmute", "brightup", "brightdown"):
            self.assertIn('name: "%s"' % name, osd)
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        for line in ("bindel = , XF86AudioRaiseVolume, global, nexus:volup", "bind = ALT, Tab, global, nexus:switchnext", "bindrn = ALT, Alt_L, global, nexus:switchdone",
                     "bind = SUPER SHIFT, S, exec, /run/current-system/sw/bin/nexus-screenshot area",
                     "bind = SUPER, left, exec, /run/current-system/sw/bin/nexus-snap left", "gesture = 3, horizontal, workspace"):
            self.assertIn(line, desktop)
        everyday = (ROOT / "nix/modules/everyday.nix").read_text()
        for line in ("hardware.bluetooth", "security.pam.services.greetd.enableGnomeKeyring", "carlito caladea liberation_ttf"):
            self.assertIn(line, everyday)
        self.assertIn('command: ["nexus-keyring-password"]', (self.out / "settings/AccountsPage.qml").read_text())
        mouse = (self.out / "settings/MousePage.qml").read_text()
        for key in ("tapToClick", "naturalScroll", "scrollSpeed", "pointerSpeed", "pointerAcceleration", "leftHanded"):
            self.assertIn('page.desktop.set("%s"' % key, mouse)
        settings = (self.out / "DesktopSettings.qml").read_text()
        self.assertIn('"keyword input:touchpad:tap-to-click " + store.tapToClick', settings)
        self.run_node_test("onscreen.test.mjs")

    def test_task_manager_tray_media_privacy_clipboard_and_emoji(self):
        tasks = (self.out / "Tasks.qml").read_text()
        self.assertIn('ender.command = ["kill", "-TERM"].concat(own.map(String))', tasks)
        self.assertIn("T.canEnd(snap, p, uid)", tasks)                  # only the person's own processes
        bar = (self.out / "TopBar.qml").read_text()
        for line in ("model: SystemTray.items", "Priv.pick(Mpris.players.values)", "Priv.inUse(privacy.streams"):
            self.assertIn(line, bar)
        clip = (self.out / "Clipboard.qml").read_text()
        self.assertIn('if (!/^\\d+$/.test(id)) return;', clip)
        emoji = (self.out / "Emoji.qml").read_text()
        self.assertIn('"emoji", e.char]', emoji)                          # passed as an argument, never in the script
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        for line in ("bind = CTRL SHIFT, Escape, global, nexus:tasks", "bind = SUPER SHIFT, V, global, nexus:clipboard",
                     "bind = SUPER, period, global, nexus:emoji"):
            self.assertIn(line, desktop)
        self.assertIn("systemd.user.services.nexus-clipboard", (ROOT / "nix/modules/everyday.nix").read_text())
        net = (self.out / "settings/NetworkPage.qml").read_text()
        self.assertIn('vpnSwitch.command = ["nmcli", "connection", up ? "up" : "down", "uuid", uuid]', net)
        self.assertIn('Quickshell.execDetached(["gio", "open", url])', net)
        self.assertIn("networking.networkmanager.plugins", (ROOT / "nix/modules/everyday.nix").read_text())
        for t in ("tasks.test.mjs", "privacy.test.mjs", "pickers.test.mjs", "network.test.mjs"):
            self.run_node_test(t)

    def test_developer_tools_run_as_the_person(self):
        page = (self.out / "settings/DeveloperPage.qml").read_text()
        self.assertIn("page.host.proposeSystem(D.VM_PLAN)", page)
        self.assertIn('copier.command = ["wl-copy", "--", text]', page)
        self.assertIn('[ ! -e \\"$HOME/.ssh/id_ed25519\\" ]', page)          # never replaces a key
        dev = (ROOT / "nix/modules/developer.nix").read_text()
        for line in ("programs.direnv", "systemd.user.sockets.podman.wantedBy", "services.gnome.gcr-ssh-agent.enable", '"dialout"'):
            self.assertIn(line, dev)
        self.assertIn('export DOCKER_HOST="unix://$XDG_RUNTIME_DIR/podman/podman.sock"', (ROOT / "nix/modules/desktop.nix").read_text())
        import json as _j
        self.assertIn("nexusos.developer.virtualMachines.enable", _j.loads((ROOT / "schema/guardian-options.json").read_text())["options"])
        self.run_node_test("developer.test.mjs")

    def test_windows_programs_get_an_answer_and_controllers_work(self):
        win = (self.out / "WindowsProgram.qml").read_text()
        self.assertIn('bridge.api("POST", "/windows-program", { fileName: fileName })', win)
        self.assertIn('IpcHandler { target: "windows"', win)
        self.assertIn('copier.command = ["wl-copy", "--", win.path]', win)
        mod = (ROOT / "nix/modules/gaming-windows.nix").read_text()
        for line in ('"application/x-ms-dos-executable"', "xdg.mime.defaultApplications", "hardware.steam-hardware.enable",
                     "hardware.xpadneo.enable", "programs.gamemode.enable"):
            self.assertIn(line, mod)
        self.assertIn("../modules/gaming-windows.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())

    def test_web_apps_cloud_drives_mail_pdf_and_spelling(self):
        apps = (self.out / "Apps.qml").read_text()
        self.assertIn('webQueue = webQueue.concat([{ summary: "Added the web app " + n, command: [webappCmd, "add", n, url] }]);', apps)
        self.assertIn('case "webapp.add":', apps)
        self.assertIn('case "webapp.add": {', (self.out / "ActionCards.qml").read_text())
        page = (self.out / "settings/AccountsPage.qml").read_text()
        self.assertIn('cloudRunner.command = [cloudCmd, "add-nextcloud", name, url, user];', page)
        self.assertIn('cloudRunner.input = appPassword;', page)        # the app password goes on stdin
        self.assertEqual(page.count("Component.onCompleted"), 1)
        mod = (ROOT / "nix/modules/online.nix").read_text()
        for line in ("pkgs.thunderbird", "pkgs.pdfarranger", "pkgs.xournalpp", 'systemd.user.services."nexus-cloud@"',
                     "environment.sessionVariables.DICPATH", '"spellchecker.dictionary_path"'):
            self.assertIn(line, mod)
        self.assertIn("../modules/online.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())
        self.run_node_test("webapps.test.mjs")
        self.run_node_test("accounts.test.mjs")

    def test_magnifier_and_colour_filters(self):
        desk = (self.out / "DesktopSettings.qml").read_text()
        self.assertIn('"keyword cursor:zoom_factor " + Math.min(4, Math.max(1, store.zoom))', desk)
        self.assertIn('"keyword decoration:screen_shader " + Access.shader(store.colourFilter, Quickshell.shellDir)', desk)
        shell = (self.out / "shell.qml").read_text()
        for name in ("zoomin", "zoomout", "zoomoff"):
            self.assertIn(f'name: "{name}"', shell)
            self.assertIn(f"global, nexus:{name}", (ROOT / "nix/modules/desktop.nix").read_text())
        filters = sorted(p.stem for p in (self.out / "filters").glob("*.frag"))
        self.assertEqual(filters, ["deuteranopia", "greyscale", "invert", "protanopia", "tritanopia"])
        tool = shutil.which("glslangValidator")
        for name in filters:
            frag = (self.out / "filters" / f"{name}.frag").read_text()
            self.assertTrue(frag.startswith("#version 300 es\n"), name)
            self.assertIn("uniform sampler2D tex;", frag)       # what Hyprland's screen shader is given
            if tool:
                run = subprocess.run([tool, str(self.out / "filters" / f"{name}.frag")], capture_output=True, text=True)
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        self.run_node_test("access.test.mjs")

    def test_alt_tab_caps_lock_theme_archives_and_developer_tools(self):
        sw = (self.out / "Switcher.qml").read_text()
        for name in ("switchnext", "switchprev", "switchdone"):
            self.assertIn(f'name: "{name}"', sw)
        self.assertIn("WlrLayershell.keyboardFocus: WlrKeyboardFocus.None", sw)      # never takes the keyboard
        self.assertIn("Switcher { theme: root.dark; apps: apps }", (self.out / "shell.qml").read_text())
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertNotIn("cyclenext", desktop)
        self.assertIn("bindrn = , Caps_Lock, global, nexus:capslock", desktop)
        self.assertIn('name: "capslock"', (self.out / "OnScreen.qml").read_text())
        everyday = (ROOT / "nix/modules/everyday.nix").read_text()
        for line in ("pkgs.p7zip pkgs.unar", 'color-scheme = "prefer-dark";', 'QT_QPA_PLATFORMTHEME = lib.mkDefault "gnome"', "pkgs.qgnomeplatform-qt6"):
            self.assertIn(line, everyday)
        # Qt follows light or dark: no style is ever forced.
        self.assertNotIn("QT_STYLE_OVERRIDE =", everyday)
        self.assertNotIn("adwaita-dark", everyday)
        self.assertIn('"dconf write /org/gnome/desktop/interface/color-scheme', (self.out / "DesktopSettings.qml").read_text())
        dev = (ROOT / "nix/modules/developer.nix").read_text()
        self.assertIn("environment.localBinInPath = lib.mkDefault true;", dev)
        self.assertIn('NPM_CONFIG_PREFIX = lib.mkDefault "$HOME/.local";', dev)
        self.assertIn("cat /proc/diskstats", (self.out / "Tasks.qml").read_text())
        self.run_node_test("switcher.test.mjs")
        self.run_node_test("tasks.test.mjs")
        self.run_node_test("onscreen.test.mjs")

    def test_startup_apps_permissions_recording_and_devices(self):
        defaults = (self.out / "settings/DefaultAppsPage.qml").read_text()
        self.assertIn('startupSetter.command = ["nexus-startup"].concat(args);', defaults)
        self.assertEqual(defaults.count("Component.onCompleted"), 1)
        privacy = (self.out / "settings/PrivacyPage.qml").read_text()
        self.assertIn('permSet.command = ["nexus-permissions"].concat(args);', privacy)
        self.assertEqual(privacy.count("Component.onCompleted"), 1)
        everyday = (ROOT / "nix/modules/everyday.nix").read_text()
        for line in ("dex --autostart --search-paths %h/.config/autostart", "services.hardware.bolt.enable",
                     "pkgs.intel-media-driver", "services.udev.packages = [ pkgs.libfido2 ];",
                     'credential = { helper = "manager"; credentialStore = "secretservice"; };'):
            self.assertIn(line, everyday)
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        for line in ("bind = SUPER ALT, R, exec, /run/current-system/sw/bin/nexus-record screen",
                     "gesture = 3, up, dispatcher, global, nexus:overview"):
            self.assertIn(line, desktop)

    def test_chinese_japanese_and_korean_typing_starts_only_when_wanted(self):
        desktop = (ROOT / "nix/modules/desktop.nix").read_text()
        self.assertIn("NEXUS_INPUT) input=\"$value\" ;;", desktop)
        self.assertIn("zh_*|ja_*|ko_*) im=1", desktop)
        self.assertIn('[ "\'\'${NEXUS_INPUT_METHOD:-}" = 1 ] && exec ${inputMethod}/bin/fcitx5 -d --replace', desktop)
        page = (self.out / "settings/RegionPage.qml").read_text()
        self.assertIn('onChose: v => page.choose("input", v)', page)

    def test_battery_care_is_a_guardian_change(self):
        import json as _j
        self.assertIn("nexusos.power.chargeLimit.enable", _j.loads((ROOT / "schema/guardian-options.json").read_text())["options"])
        battery = (ROOT / "nix/modules/battery.nix").read_text()
        self.assertIn('ATTR{charge_control_end_threshold}="80"', battery)
        self.assertIn("../modules/battery.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())
        self.assertIn("page.host.proposeSystem(P.chargeLimitPlan(", (self.out / "settings/PowerPage.qml").read_text())
        self.run_node_test("power.test.mjs")

    def test_restart_into_windows_and_game_answers(self):
        power = (self.out / "settings/PowerPage.qml").read_text()
        self.assertIn('Quickshell.execDetached(["systemctl", "reboot", "--boot-loader-entry=" + page.windowsEntry])', power)
        self.assertIn('case "power.windows": windowsBoot.running = true;', (self.out / "ActionCards.qml").read_text())
        self.assertIn("win.game = data.game || null", (self.out / "WindowsProgram.qml").read_text())
        everyday = (ROOT / "nix/modules/everyday.nix").read_text()
        self.assertIn("boot.loader.timeout = lib.mkDefault 0;", everyday)       # no start-up menu: Windows is an app
        self.assertIn('ExecStart = "${windows}/bin/nexus-windows entry";', everyday)
        self.assertIn("import Quickshell.Io", (self.out / "ActionCards.qml").read_text())
        vm = (ROOT / "scripts/desktop/nexus-windows-vm.sh").read_text()
        self.assertIn("uri=qemu:///session", vm)                                # as the person, no password
        self.assertIn("--tpm backend.type=emulator,backend.version=2.0,model=tpm-crb", vm)
        self.assertIn("virtualisation.libvirtd.qemu.swtpm.enable = true;", (ROOT / "nix/modules/developer.nix").read_text())
        self.assertIn('Quickshell.execDetached(["nexus-windows-vm"])', (self.out / "settings/DeveloperPage.qml").read_text())

    def test_copy_text_from_the_screen_and_mobile_hotspot(self):
        self.assertIn("bind = SUPER SHIFT, T, exec, /run/current-system/sw/bin/nexus-ocr", (ROOT / "nix/modules/desktop.nix").read_text())
        ocr = (ROOT / "scripts/desktop/nexus-ocr.sh").read_text()
        self.assertIn('tesseract "$shot" - -l', ocr)                          # read on this computer
        self.assertNotIn("curl", ocr)
        net = (self.out / "settings/NetworkPage.qml").read_text()
        self.assertIn("nmcli device wifi hotspot con-name Hotspot", net)
        self.assertIn("tr -dc 'abcdefghjkmnpqrstuvwxyz23456789' </dev/urandom", net)

    def test_sharing_folders_is_a_guardian_change_and_read_only(self):
        import json as _j
        self.assertIn("nexusos.sharing.enable", _j.loads((ROOT / "schema/guardian-options.json").read_text())["options"])
        share = (ROOT / "scripts/desktop/nexus_share.py").read_text()
        self.assertIn('"Everyone:R", "guest_ok=y"', share)                       # others can read, never change
        net = (self.out / "settings/NetworkPage.qml").read_text()
        self.assertIn("page.host.proposeSystem(Net.SHARING_PLAN)", net)
        self.assertIn("../modules/sharing.nix", (ROOT / "nix/profiles/nexusos.nix").read_text())
        self.run_node_test("network.test.mjs")

    def test_start_fresh_keeps_files_and_can_be_undone(self):
        reset = (ROOT / "scripts/desktop/nexus-reset-settings.sh").read_text()
        self.assertIn('mv "$current" "$config/nexusos-before-reset-', reset)          # moved aside, never deleted
        self.assertNotIn("rm -", reset)
        self.assertIn('Quickshell.execDetached(["nexus-reset-settings", "undo"])', (self.out / "settings/AboutPage.qml").read_text())

    def test_fingerprint_unlocks_and_approves_but_login_keeps_the_password(self):
        fp = (ROOT / "nix/modules/fingerprint.nix").read_text()
        for line in ("security.pam.services.greetd.fprintAuth = false;", "security.pam.services.login.fprintAuth = false;"):
            self.assertIn(line, fp)                         # the password unlocks the keyring and the disk
        self.assertIn("enabled = ${lib.boolToString config.services.fprintd.enable}", (ROOT / "nix/modules/desktop.nix").read_text())
        self.assertIn("page.host.proposeSystem(A.FINGERPRINT_PLAN)", (self.out / "settings/AccountsPage.qml").read_text())
        self.run_node_test("accounts.test.mjs")

    def test_feedback_is_opt_in_and_shown_before_it_is_sent(self):
        page = (self.out / "settings/FeedbackPage.qml").read_text()
        self.assertIn('Quickshell.execDetached(["xdg-open", F.issueUrl(page.built.title, page.built.body)])', page)
        self.assertIn("text: page.built.title + \"\\n\\n\" + page.built.body", page)   # the preview is the report
        self.assertNotIn("curl", page)
        watcher = (self.out / "CrashWatcher.qml").read_text()
        self.assertIn("running: !!watcher.desktop && watcher.desktop.crashNotices", watcher)
        self.assertIn("property bool crashNotices: false", (self.out / "DesktopSettings.qml").read_text())
        self.run_node_test("feedback.test.mjs")

    def test_help_is_the_user_guide_and_follows_only_safe_links(self):
        self.assertEqual((ROOT / "docs/USER-GUIDE.md").read_text(), (self.out / "help/user-guide.md").read_text())
        page = (self.out / "settings/HelpPage.qml").read_text()
        self.assertIn("onLinkActivated: href => page.follow(href)", page)
        self.assertIn("const l = H.link(href, pageIds);", page)
        self.assertIn("../../docs/USER-GUIDE.md", (ROOT / "nix/packages/nexus-shell.nix").read_text())
        self.run_node_test("help.test.mjs")

if __name__ == "__main__":
    unittest.main()
