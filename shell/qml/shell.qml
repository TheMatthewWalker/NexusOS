// Nexus shell: the SpaceUI desktop for Hyprland, in Quickshell (ADR-003).
// It draws the wallpaper, the top bar, the bottom Nexus launcher, the full
// Nexus core with its app orbit and chat, the orbital controls in the
// bottom-right corner, and Settings. Hyprland manages the windows: floating
// by default (Desktop B), or tiled (Settings, Desktop).
//
// Safety rules it keeps (docs/design/spaceui/README.md):
//  - the AI only suggests: apps open only when the user picks them;
//  - status is never faked: offline Core, missing apps and unavailable
//    services are shown as such;
//  - surfaces sit on the Top layer, below lock screens;
//  - model output is plain text.
import QtQuick
import QtQuick.Shapes
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Hyprland
import "lib/game.mjs" as G
import "lib/orbit.mjs" as O
import "lib/tokens.mjs" as T

ShellRoot {
    id: root
    readonly property var tk: T.tokens.baseline
    readonly property var lay: T.layout
    readonly property bool reduced: Quickshell.env("NEXUS_REDUCED_MOTION") === "1"
    property bool expanded: false
    property bool showAll: false
    property var suggested: []
    property string suggestedBy: ""
    property string mode: "idle"            // idle | typing | thinking | responding
    property string reply: ""
    property string provider: ""
    property string error: ""
    property int chatId: -1
    property string lastPrompt: ""
    property string toolNote: ""            // a look-up Nexus is running, e.g. "Searching your files"
    property int suggestId: -1
    property real voice: 0
    // GPU budget (the local model shares the GPU with the core):
    // - while the model is generating, the big core caps its frame rate and detail;
    // - the launcher core stops drawing while the screen is idle or a window is fullscreen.
    // simulateBusy lets the benchmark hold the busy state without a chat.
    property bool simulateBusy: false
    readonly property bool modelBusy: simulateBusy || mode === "thinking" || mode === "responding"
    readonly property bool fullscreenActive: ToplevelManager.activeToplevel ? ToplevelManager.activeToplevel.fullscreen : false
    readonly property bool launcherPaused: idle.isIdle || fullscreenActive || gameMode
    // Game mode (docs/design/features.md, 11): while a fullscreen game is open,
    // the model is unloaded, the launcher core stops and Hyprland drops blur and
    // animations. The model reloads on the next question, never before.
    readonly property bool gameModeEnabled: Quickshell.env("NEXUS_GAME_MODE") !== "0"
    property bool fullscreenIsGame: Quickshell.env("NEXUS_GAME_ANY_FULLSCREEN") === "1"
    readonly property string gameApp: !gameModeEnabled ? "" : G.activeGame(
        ToplevelManager.toplevels.values.map(t => ({ appId: t.appId, fullscreen: t.fullscreen })), fullscreenIsGame)
    readonly property bool gameMode: gameApp !== ""
    property int unloadId: -1
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    onGameModeChanged: gameMode ? enterGameMode() : exitGameMode()
    Component.onCompleted: if (gameMode) enterGameMode()
    // Provisional dark theme (approved 22 Oct): deep space, light ink, glass surfaces.
    // High contrast (Settings, Accessibility): solid panels, white text, bright borders.
    readonly property var dark: desktop.highContrast ? ({
        ink: "#FFFFFF", inkSecondary: "#DCE4FF", glass: Qt.rgba(0, 0, 0, 0.96),
        glassBorder: "#B7CAFF", focus: "#FFD166", primary: "#5A48F0", chip: "#05070F",
        orbit: Qt.rgba(0.8, 0.85, 1.0, 0.6), error: "#FFB3C1", muted: "#C9D4FF"
    }) : ({
        ink: "#E3EAFF", inkSecondary: "#9AA9C9", glass: Qt.rgba(0.04, 0.07, 0.15, 0.80),
        glassBorder: "#2A3A66", focus: "#86A8FF", primary: "#7C6BFF", chip: "#121B33",
        orbit: Qt.rgba(0.55, 0.62, 1.0, 0.22), error: "#FF8FA3", muted: "#7885A3"
    })

    function visibleApps() {
        if (showAll) return O.APP_IDS;
        return O.APP_IDS.filter(id => suggested.includes(id));
    }
    function open() { expanded = true }
    function collapse() {
        expanded = false; showAll = false; suggested = []; suggestedBy = "";
        input.text = ""; reply = ""; error = ""; mode = "idle"; voice = 0; toolNote = ""; actionCards.clear();
    }
    function ask(prompt) {
        if (!bridge.online) return;
        error = ""; reply = ""; provider = ""; mode = "thinking"; toolNote = ""; actionCards.clear();
        lastPrompt = prompt;
        chatId = bridge.chat(prompt);
    }
    function openApp(id) {
        // Settings is part of the shell itself.
        if (id === "settings") { collapse(); settings.open("", ""); return; }
        // Studio: making something new, images or code (each window switches to the other).
        if (id === "studio") { collapse(); imagesWin.open(); return; }
        if (apps.open(id)) collapse();
    }
    function installed(id) { return id === "settings" || id === "studio" || apps.installed(id) }
    // Switching to a window from the launcher. A minimised window (the title
    // bar's minimise button moves it to the hidden "minimized" workspace)
    // comes back to the current workspace first.
    function activateWindow(toplevel) {
        const h = Hyprland.toplevels.values.find(t => t.wayland === toplevel);
        if (h && h.workspace && h.workspace.name.startsWith("special:") && h.address) {
            const address = h.address.startsWith("0x") ? h.address : "0x" + h.address;
            const ws = Hyprland.focusedWorkspace ? Hyprland.focusedWorkspace.id : 1;
            Hyprland.dispatch("movetoworkspace " + ws + ",address:" + address);
        }
        toplevel.activate();
    }
    function enterGameMode() {
        hypr.exec([hyprctl, "--batch", "keyword decoration:blur:enabled false ; keyword animations:enabled false"]);
        unloadId = bridge.online ? bridge.unload() : -1;
        if (unloadId === -1) gameToast(G.toastText(false, null));
    }
    function exitGameMode() {
        unloadId = -1;
        // Back to the person's own choices (Settings, Desktop).
        hypr.exec([hyprctl, "--batch", "keyword decoration:blur:enabled " + desktop.blur
                                       + " ; keyword animations:enabled " + desktop.animations]);
    }
    // Hyprland draws fullscreen windows above the Top layer, so the toast is a
    // compositor notification: it shows over the game without the shell
    // putting a surface on the Overlay layer.
    function gameToast(text) { toast.exec([hyprctl, "notify", "-1", "5000", "rgb(86a8ff)", text]) }

    CoreBridge {
        id: bridge
        onDelta: (id, text) => {
            if (id !== root.chatId) return;
            root.mode = "responding";
            root.reply += text;
            // The core speaks the reply as it streams: vowel-rich pieces peak.
            const vowels = (text.match(/[aeiouy]/gi) || []).length;
            root.voice = Math.min(1, 0.35 + vowels / Math.max(4, text.length));
            voiceDecay.restart();
        }
        // Nexus's actions (NexusActions.cs in Core): a look-up running, or a card for the person.
        onTool: (id, ev) => { if (id === root.chatId) root.toolNote = ev.status === "running" ? (ev.summary || "Working") + "…" : "" }
        onAction: (id, ev) => { if (id === root.chatId) { root.toolNote = ""; actionCards.offer(ev) } }
        onDone: (id, provider) => {
            if (id !== root.chatId) return;
            root.provider = provider; root.toolNote = "";
            settle.restart();
            // With the answer done the model is free: let it pick the apps,
            // falling back to the rules (and saying so) if it cannot.
            root.suggestId = bridge.suggest(root.lastPrompt, true);
            voiceCtl.speak(root.reply);         // spoken replies, when turned on (Settings, Accessibility)
        }
        onUnloaded: (id, freed) => {
            if (id !== root.unloadId) return;
            root.unloadId = -1;
            root.gameToast(G.toastText(true, freed));
        }
        onFailed: (id, message) => {
            if (id === root.unloadId && id !== -1) {
                // Nothing to free (a cloud provider) or Core could not unload: say only what is true.
                root.unloadId = -1;
                root.gameToast(G.toastText(false, null));
                return;
            }
            if (id !== root.chatId && id !== -1) return;
            root.error = message; root.mode = "idle"; root.voice = 0;
        }
        onSuggestions: (id, list, source) => {
            if (id !== root.suggestId) return;
            root.suggested = list.map(a => a.id).filter(x => O.APP_IDS.includes(x));
            root.suggestedBy = source;
        }
    }
    AppDirectory { id: apps }
    DesktopSettings { id: desktop; gameMode: root.gameMode; bridge: bridge }
    RuleRunner { bridge: bridge; desktop: desktop; wallpaper: wallpaper; apps: apps; wifi: settings.wifi }
    // Voice control and Describe this, on this computer (Voice.qml, Describe.qml).
    Voice { id: voiceCtl; theme: root.dark; bridge: bridge
            onHeard: text => { root.open(); input.text = text; root.ask(text) } }
    Describe { id: describer; bridge: bridge }
    Connections {
        target: describer
        function onDescribed(tag, text) { if (tag === "nexus") root.showDescription(text) }
        function onFailed(tag, message) { if (tag === "nexus") { root.open(); root.reply = ""; root.error = message } }
    }
    // A description from the local vision model, shown (and read out) as Nexus's answer.
    function showDescription(text) {
        open();
        error = ""; toolNote = ""; mode = "idle"; reply = text; provider = "local vision model";
        voiceCtl.speak(text);
    }
    Settings {
        id: settings
        theme: root.dark; bridge: bridge; desktop: desktop; reduced: root.reduced
        voice: voiceCtl; describer: describer
        onAskNexus: text => { root.open(); input.text = text; root.ask(text) }
        onFixIt: problem => fixItWin.open(problem)
        onOpenWelcome: firstRun.open()
        onMoveIn: moveInWin.open()
    }
    Apps { id: appsWin; theme: root.dark; bridge: bridge }
    FixIt { id: fixItWin; theme: root.dark; bridge: bridge }
    Search { id: searchWin; theme: root.dark; bridge: bridge; onChooseFolders: settings.open("search", "") }
    Overview { id: overview; theme: root.dark; bridge: bridge; apps: apps; reduced: root.reduced }
    FirstRun { id: firstRun; theme: root.dark; bridge: bridge; desktop: desktop; onMoveIn: moveInWin.open()
               onProposeSystem: plan => guardianWin.openPlan(plan, "user") }
    MoveIn { id: moveInWin; theme: root.dark }
    Explain { id: explainCard; theme: root.dark; bridge: bridge; describer: describer; onFixIt: problem => fixItWin.open(problem)
              onTellMeMore: { root.open(); input.text = "Tell me more about that."; root.ask(input.text) } }
    Code { id: codeWin; theme: root.dark; bridge: bridge; onSwitchToImages: imagesWin.open() }
    Images { id: imagesWin; theme: root.dark; bridge: bridge; onSwitchToCode: codeWin.open("")
             onUseAsWallpaper: path => wallpaper.set(path) }
    // System changes waiting for approval, and how they are going (Guardian.qml).
    Guardian { id: guardianWin; theme: root.dark; bridge: bridge }
    TopBar { theme: root.dark; settings: settings; guardian: guardianWin; onOpenNexus: root.expanded ? root.collapse() : root.open()
             onOpenApps: appsWin.open("all") }
    OrbitalControls { theme: root.dark; settings: settings; reduced: root.reduced }
    Process { id: hypr }
    Process { id: toast }
    Wallpaper { id: wallpaper }

    Timer { id: voiceDecay; interval: 140; onTriggered: root.voice = 0.1 }
    Timer { id: settle; interval: 1200; onTriggered: { root.mode = "idle"; root.voice = 0 } }
    Timer {
        id: debounce
        interval: root.tk.appOrbit.motion.intentDebounce
        onTriggered: {
            const text = input.text.trim();
            if (!text) { root.suggested = []; return }
            if (bridge.online) root.suggestId = bridge.suggest(text, false);
        }
    }

    // Super+Space (bind = SUPER, Space, global, nexus:toggle) and
    // `quickshell ipc call nexus toggle` both open and close Nexus.
    GlobalShortcut { appid: "nexus"; name: "toggle"; onPressed: root.expanded ? root.collapse() : root.open() }
    // Super+I (bind = SUPER, I, global, nexus:settings).
    GlobalShortcut { appid: "nexus"; name: "settings"; onPressed: settings.open("", "") }
    // Super+C (bind = SUPER, C, global, nexus:code): the coding agent.
    GlobalShortcut { appid: "nexus"; name: "code"; onPressed: codeWin.open("") }
    // Super+G (bind = SUPER, G, global, nexus:images): image generation.
    GlobalShortcut { appid: "nexus"; name: "images"; onPressed: imagesWin.open() }
    // Super+A (bind = SUPER, A, global, nexus:apps): every app, and getting new ones.
    GlobalShortcut { appid: "nexus"; name: "apps"; onPressed: appsWin.visible ? appsWin.visible = false : appsWin.open("all") }
    // Super+S (bind = SUPER, S, global, nexus:search): private search over the chosen folders.
    GlobalShortcut { appid: "nexus"; name: "search"; onPressed: searchWin.visible ? searchWin.visible = false : searchWin.open() }
    // Super+Tab (bind = SUPER, Tab, global, nexus:overview): every workspace around the core.
    GlobalShortcut { appid: "nexus"; name: "overview"; onPressed: overview.open ? overview.hide() : overview.show() }
    // Super+? (bind = SUPER SHIFT, slash, global, nexus:explain): point at something to have it explained.
    GlobalShortcut { appid: "nexus"; name: "explain"; onPressed: explainCard.start() }
    // Super+V (bind = SUPER, V, global, nexus:talk): push-to-talk, the microphone open only while it is held.
    GlobalShortcut { appid: "nexus"; name: "talk"; onPressed: voiceCtl.press(); onReleased: voiceCtl.release() }
    IpcHandler {
        target: "nexus"
        function toggle(): void { root.expanded ? root.collapse() : root.open() }
        function state(): string {
            return JSON.stringify({ expanded: root.expanded, online: bridge.online, mode: root.mode, orbit: root.visibleApps(),
                                    modelBusy: root.modelBusy, launcherPaused: root.launcherPaused, gameMode: root.gameMode })
        }
        function simulateBusy(on: bool): void { root.simulateBusy = on }
        // Ask Nexus as if typed in the chat; actions() lists the cards it offered.
        function ask(text: string): void { root.open(); input.text = text; root.ask(text) }
        function actions(): string { return JSON.stringify(actionCards.cardList.map(c => ({ summary: c.summary, safety: c.safety, state: c.state, message: c.message }))) }
        // "Treat fullscreen as game": any fullscreen window except video players and browsers.
        function fullscreenIsGame(on: bool): void { root.fullscreenIsGame = on }
    }
    IdleMonitor { id: idle; timeout: 60; respectInhibitors: true }

    // Keeps windows clear of the launcher at the bottom edge; draws nothing.
    PanelWindow {
        anchors { bottom: true; left: true; right: true }
        implicitHeight: 1
        color: "transparent"
        exclusiveZone: root.tk.window.workArea.bottomReservation
        WlrLayershell.namespace: "nexus-reserve"
        mask: Region {}
    }

    PanelWindow {
        id: panel
        anchors.bottom: true
        implicitWidth: Math.round(root.lay.surface.width * fit)
        implicitHeight: Math.round(root.lay.surface.height * fit)
        // The layout is drawn for a 760x800 surface. On a shorter or narrower
        // screen it is scaled down to fit, so the core stays on screen; input
        // regions follow, as Region maps its item through the scale.
        readonly property var fitScreen: panel.screen || Quickshell.screens[0]
        readonly property real fit: !fitScreen ? 1 : Math.min(1,
            fitScreen.height / root.lay.surface.height, fitScreen.width / root.lay.surface.width)
        color: "transparent"
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.layer: WlrLayer.Top            // below lock and secure surfaces
        WlrLayershell.namespace: "nexus-spaceui"
        // While Nexus is open it takes the keyboard, like a launcher: typing goes
        // straight to the chat; Escape or a click outside gives it back.
        WlrLayershell.keyboardFocus: root.expanded ? WlrKeyboardFocus.Exclusive : WlrKeyboardFocus.None
        // Only the drawn parts take input; the rest of the surface passes clicks through.
        mask: Region { item: root.expanded ? fullHit : launcherHit }

        Item {
            id: content
            width: root.lay.surface.width; height: root.lay.surface.height
            scale: panel.fit; transformOrigin: Item.TopLeft

            Item { id: fullHit; anchors.fill: parent }
            Item {
                id: launcherHit
                x: root.lay.launcher.x - w / 2; y: root.lay.launcher.y - h + 44
                property real w: launcher.hoverOpen ? 300 : 120
                property real h: launcher.hoverOpen ? 190 : 100
                width: w; height: h
            }

            // Click outside the content collapses the expanded Nexus.
            MouseArea { anchors.fill: parent; enabled: root.expanded; onClicked: root.collapse() }

            // ---- collapsed: the bottom launcher with open windows as tiny planets ----
            Item {
                id: launcher
                visible: !root.expanded
                x: root.lay.launcher.x; y: root.lay.launcher.y
                property bool hoverOpen: false
                readonly property var windows: ToplevelManager.toplevels.values

                Timer { id: hoverDelay; interval: root.lay.launcherHoverDelay; onTriggered: launcher.hoverOpen = true }
                Item {
                    x: -launcherHit.w / 2; y: -launcherHit.h + 44; width: launcherHit.w; height: launcherHit.h
                    HoverHandler {
                        id: launcherHover
                        onHoveredChanged: {
                            if (hovered) hoverDelay.start();
                            else { hoverDelay.stop(); launcher.hoverOpen = false }
                        }
                    }
                }
                NovaCore {
                    id: miniCore
                    width: 300; height: 190
                    x: -150; y: -146
                    centre: Qt.point(150, 146)
                    radius: root.lay.launcher.diameter * 0.4
                    steps: 24
                    idleFps: 30
                    paused: root.launcherPaused
                    reduced: root.reduced
                    hovered: launcherHover.hovered
                }
                Item {
                    width: root.lay.launcher.diameter; height: width
                    x: -width / 2; y: -height / 2
                    TapHandler { onTapped: root.open() }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Open Nexus"
                }
                Repeater {
                    model: launcher.windows
                    delegate: Item {
                        required property var modelData
                        required property int index
                        readonly property var p: O.launcherPlanet(index, launcher.windows.length, launcher.hoverOpen)
                        readonly property string icon: apps.iconFor(modelData.appId)
                        x: p.x; y: p.y
                        Behavior on x { NumberAnimation { duration: root.reduced ? 0 : 260; easing.type: Easing.OutCubic } }
                        Behavior on y { NumberAnimation { duration: root.reduced ? 0 : 260; easing.type: Easing.OutCubic } }
                        Rectangle {
                            width: p.size; height: p.size; radius: width / 2
                            x: -width / 2; y: -height / 2
                            color: root.dark.glass; border.color: modelData.activated ? root.dark.focus : root.dark.glassBorder; border.width: 1
                            Behavior on width { NumberAnimation { duration: root.reduced ? 0 : 260 } }
                            Image {
                                anchors.centerIn: parent
                                width: parent.width * 0.6; height: width
                                visible: launcher.hoverOpen && parent.parent.icon !== ""
                                source: parent.parent.icon
                                sourceSize: Qt.size(64, 64)
                            }
                            Text {
                                anchors.centerIn: parent
                                visible: launcher.hoverOpen && parent.parent.icon === ""
                                text: (modelData.title || modelData.appId || "?").slice(0, 1).toUpperCase()
                                font.pixelSize: 18; font.weight: 600; color: root.dark.ink
                            }
                            TapHandler { enabled: launcher.hoverOpen; onTapped: root.activateWindow(modelData) }
                            Accessible.role: Accessible.Button
                            Accessible.name: "Switch to " + modelData.title
                        }
                        Text {
                            visible: launcher.hoverOpen
                            y: p.size / 2 + 4; x: -width / 2
                            width: Math.min(implicitWidth, 120); elide: Text.ElideRight
                            text: modelData.title || modelData.appId
                            font.pixelSize: 13; color: root.dark.ink
                        }
                    }
                }
            }

            // ---- expanded: full core, orbit and chat ----
            Item {
                id: stage
                anchors.fill: parent
                visible: root.expanded
                opacity: root.expanded ? 1 : 0
                Behavior on opacity { NumberAnimation { duration: root.reduced ? 0 : 220 } }

                // A nebula behind the core and the chat keeps them legible over any window.
                Nebula { anchors.fill: parent; z: -2; seed: 7; depth: 0.86 }
                NovaCore {
                    id: bigCore
                    anchors.fill: parent
                    z: -1
                    centre: Qt.point(root.lay.core.x, root.lay.core.y)
                    radius: root.tk.nexus.hitDiameter / 2
                    // Fewer raymarch samples for machines without a capable GPU.
                    steps: Number(Quickshell.env("NEXUS_CORE_STEPS")) || 56
                    busy: root.modelBusy
                    reduced: root.reduced
                    mode: ({ idle: "idle", typing: "listening", thinking: "thinking", responding: "speaking" })[root.mode]
                    voice: root.voice
                    hovered: coreHover.hovered
                }
                Shape {
                    x: root.lay.core.x; y: root.lay.core.y
                    preferredRendererType: Shape.CurveRenderer
                    ShapePath {
                        strokeColor: root.dark.orbit; strokeWidth: 1; fillColor: "transparent"
                        PathAngleArc { radiusX: root.tk.appOrbit.radius; radiusY: radiusX; sweepAngle: 360 }
                    }
                    ShapePath {
                        strokeColor: Qt.rgba(0.55, 0.62, 1.0, 0.16); strokeWidth: 1; fillColor: "transparent"
                        strokeStyle: ShapePath.DashLine; dashPattern: [2, 7]
                        PathAngleArc { radiusX: root.tk.appOrbit.innerRadius; radiusY: radiusX; sweepAngle: 360 }
                    }
                }
                // Touching the core: hover wakes it, a tap brings every app into orbit, a drag turns it.
                Item {
                    width: root.tk.nexus.hitDiameter; height: width
                    x: root.lay.core.x - width / 2; y: root.lay.core.y - height / 2
                    activeFocusOnTab: true
                    Keys.onReturnPressed: { bigCore.ripple(); root.showAll = !root.showAll }
                    Keys.onSpacePressed: { bigCore.ripple(); root.showAll = !root.showAll }
                    Keys.onEscapePressed: root.collapse()
                    Rectangle {
                        anchors.fill: parent; radius: width / 2; color: "transparent"
                        border.color: root.dark.focus; border.width: 3; visible: parent.activeFocus
                    }
                    HoverHandler { id: coreHover }
                    TapHandler { onTapped: { bigCore.ripple(); root.showAll = !root.showAll } }
                    DragHandler {
                        target: null
                        property point last: Qt.point(0, 0)
                        onActiveChanged: last = Qt.point(0, 0)
                        onTranslationChanged: { bigCore.turn(translation.x - last.x, translation.y - last.y); last = Qt.point(translation.x, translation.y) }
                    }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Show or hide all apps around Nexus"
                }
                Column {
                    x: root.lay.core.x - width / 2; y: root.lay.core.y + 98
                    spacing: 4
                    Text { anchors.horizontalCenter: parent.horizontalCenter; text: "Nexus AI"
                           font.pixelSize: root.tk.typography.coreTitle.fontSize; font.weight: 550
                           font.letterSpacing: root.tk.typography.coreTitle.letterSpacing; color: root.dark.ink }
                    Text {
                        anchors.horizontalCenter: parent.horizontalCenter
                        text: !bridge.online ? "Offline · apps still work"
                            : ({ idle: "Ready when you are", typing: "Listening", thinking: "Thinking…", responding: "Responding" })[root.mode]
                        font.pixelSize: 13; color: bridge.online ? root.dark.inkSecondary : root.dark.error
                    }
                }

                Repeater {
                    model: O.APP_IDS
                    delegate: Item {
                        id: node
                        required property string modelData
                        required property int index
                        readonly property bool shown: root.expanded && root.visibleApps().includes(modelData)
                        readonly property bool available: root.installed(modelData)
                        property real progress: 0
                        readonly property real eased: shown ? O.entryEase(progress) : 1 - O.exitEase(1 - progress)
                        readonly property var at: root.reduced ? Object.assign(O.slot(modelData), { scale: 1, opacity: progress })
                                                               : O.emergence(modelData, eased)
                        readonly property bool settled: shown && progress >= 1
                        x: root.lay.core.x + at.x; y: root.lay.core.y + at.y
                        opacity: at.opacity; scale: at.scale
                        visible: progress > 0
                        onShownChanged: {
                            anim.stop();
                            anim.delay = shown && !root.reduced ? index * root.tk.appOrbit.motion.entryStagger : 0;
                            anim.to = shown ? 1 : 0;
                            anim.duration = root.reduced ? root.tk.appOrbit.motion.reducedEntryExit
                                          : shown ? root.tk.appOrbit.motion.entry : root.tk.appOrbit.motion.exit;
                            anim.start();
                        }
                        SequentialAnimation {
                            id: anim
                            property int delay: 0
                            property real to: 1
                            property int duration: 850
                            PauseAnimation { duration: anim.delay }
                            NumberAnimation { target: node; property: "progress"; to: anim.to; duration: anim.duration }
                        }
                        Rectangle {
                            width: root.tk.appOrbit.buttonDiameter; height: width; radius: width / 2
                            x: -width / 2; y: -height / 2
                            color: root.dark.glass
                            opacity: node.available ? 1 : 0.55
                            // Keyboard: Tab reaches each settled app, Enter or Space opens it.
                            activeFocusOnTab: node.settled
                            Keys.onReturnPressed: if (node.available) root.openApp(node.modelData)
                            Keys.onSpacePressed: if (node.available) root.openApp(node.modelData)
                            Keys.onEscapePressed: root.collapse()
                            border.color: activeFocus || (btnHover.hovered && node.settled && node.available) ? root.dark.focus : root.dark.glassBorder
                            border.width: activeFocus ? 3 : 1.5
                            Image { anchors.centerIn: parent; width: 30; height: 30; sourceSize: Qt.size(60, 60)
                                    source: "icons/" + node.modelData + ".svg" }
                            HoverHandler { id: btnHover; enabled: node.settled }
                            TapHandler { enabled: node.settled && node.available; onTapped: root.openApp(node.modelData) }
                            Accessible.role: Accessible.Button
                            Accessible.name: node.available ? "Open " + O.APP_LABELS[node.modelData]
                                                            : O.APP_LABELS[node.modelData] + ", not installed"
                        }
                        Column {
                            y: root.tk.appOrbit.buttonDiameter / 2 + root.tk.appOrbit.labelGap; x: -width / 2
                            Text {
                                anchors.horizontalCenter: parent.horizontalCenter
                                text: O.APP_LABELS[node.modelData]; font.pixelSize: 13; font.weight: 500
                                color: node.available ? root.dark.ink : root.dark.inkSecondary
                            }
                            Text {
                                anchors.horizontalCenter: parent.horizontalCenter
                                visible: !node.available
                                text: "Not installed"; font.pixelSize: 12; color: root.dark.muted
                            }
                        }
                    }
                }

                // Chat bar
                Rectangle {
                    id: chat
                    x: root.lay.chat.x; y: root.lay.chat.y
                    width: root.lay.chat.width; height: root.lay.chat.height
                    radius: root.tk.radii.chat; color: root.dark.glass
                    border.color: input.activeFocus ? root.dark.focus : root.dark.glassBorder; border.width: 1
                    Image { x: 22; anchors.verticalCenter: parent.verticalCenter; width: 22; height: 22
                            source: "icons/spark.svg"; sourceSize: Qt.size(44, 44) }
                    TextInput {
                        id: input
                        x: 58; width: parent.width - 130 - root.tk.chat.sendDiameter - 8; anchors.verticalCenter: parent.verticalCenter
                        font.pixelSize: 14; color: root.dark.ink; clip: true
                        readOnly: !bridge.online
                        focus: root.expanded
                        Text {
                            visible: !input.text
                            text: bridge.online ? "Ask Nexus AI or find anything" : "Nexus Core is offline. Apps above still open."
                            font.pixelSize: 14; color: root.dark.inkSecondary
                        }
                        onTextEdited: { root.mode = text ? "typing" : "idle"; debounce.restart() }
                        onAccepted: if (text.trim()) root.ask(text.trim())
                        activeFocusOnTab: true
                        Keys.onEscapePressed: { voiceCtl.stopSpeaking(); root.collapse() }
                        Accessible.name: "Ask Nexus AI"
                    }
                    MouseArea { anchors.fill: input; onClicked: input.forceActiveFocus(); cursorShape: Qt.IBeamCursor }
                    // Push-to-talk: hold to speak, or click once to start and again to send (Voice.qml).
                    Rectangle {
                        visible: voiceCtl.pushToTalk
                        width: root.tk.chat.sendDiameter; height: width; radius: width / 2
                        anchors.right: parent.right; anchors.rightMargin: 18 + root.tk.chat.sendDiameter; anchors.verticalCenter: parent.verticalCenter
                        color: voiceCtl.listening ? "#D93A5B" : root.dark.chip
                        border.color: voiceCtl.listening ? "#FF5C7A" : root.dark.glassBorder; border.width: 1
                        Image { anchors.centerIn: parent; width: 20; height: 20; source: "icons/mic.svg"; sourceSize: Qt.size(40, 40) }
                        TapHandler {
                            enabled: bridge.online
                            onPressedChanged: pressed ? voiceCtl.press() : voiceCtl.release()
                        }
                        Accessible.role: Accessible.Button
                        Accessible.name: voiceCtl.listening ? "Microphone on: press to send" : "Speak to Nexus (hold, or press Super+V)"
                    }
                    Rectangle {
                        width: root.tk.chat.sendDiameter; height: width; radius: width / 2
                        anchors.right: parent.right; anchors.rightMargin: 10; anchors.verticalCenter: parent.verticalCenter
                        color: bridge.online ? root.dark.primary : root.dark.glassBorder
                        Image { anchors.centerIn: parent; width: 20; height: 20; source: "icons/send.svg"; sourceSize: Qt.size(40, 40) }
                        TapHandler { enabled: bridge.online; onTapped: if (input.text.trim()) root.ask(input.text.trim()) }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Send"
                    }
                }

                // Reply, with its sources kept visibly separate. It scrolls: while the
                // answer streams in it follows the newest line, unless the user has
                // scrolled back up to read.
                Flickable {
                    id: replyView
                    x: root.lay.reply.x + 12; y: root.lay.reply.y
                    width: root.lay.reply.width - 24
                    height: root.lay.surface.height - root.lay.reply.y - 12
                    contentWidth: width; contentHeight: replyColumn.implicitHeight
                    clip: true
                    boundsBehavior: Flickable.StopAtBounds
                    visible: root.reply.length > 0 || root.error.length > 0 || root.toolNote !== "" || actionCards.cardList.length > 0
                    property bool follow: true
                    onMovementEnded: follow = atYEnd
                    onContentHeightChanged: if (follow) contentY = Math.max(0, contentHeight - height)
                    Connections {
                        target: root
                        function onChatIdChanged() { replyView.follow = true; replyView.contentY = 0 }
                    }
                    WheelHandler {
                        onWheel: event => {
                            replyView.contentY = Math.max(0, Math.min(replyView.contentHeight - replyView.height,
                                                                     replyView.contentY - event.angleDelta.y / 2));
                            replyView.follow = replyView.atYEnd;
                        }
                    }
                    Keys.onUpPressed: { follow = false; contentY = Math.max(0, contentY - 40) }
                    Keys.onDownPressed: { contentY = Math.min(Math.max(0, contentHeight - height), contentY + 40); follow = atYEnd }
                    Column {
                        id: replyColumn
                        width: replyView.width - 10
                        spacing: 8
                        Text {
                            id: replyText
                            visible: text !== ""             // a reply can be just an action card
                            width: parent.width; wrapMode: Text.Wrap
                            textFormat: Text.PlainText      // model output is never rendered as rich text
                            text: root.error ? "Nexus Core could not answer: " + root.error : root.reply
                            font.pixelSize: 14; lineHeight: 1.5; color: root.error ? root.dark.error : root.dark.ink
                        }
                        Text {
                            visible: root.toolNote !== "" && !root.error
                            text: root.toolNote
                            font.pixelSize: 13; font.italic: true; color: root.dark.inkSecondary
                        }
                        // What Nexus offers to do: nothing happens until the person picks "Do it".
                        ActionCards {
                            id: actionCards
                            width: parent.width
                            theme: root.dark; bridge: bridge; apps: apps; settings: settings; desktop: desktop
                            appsWin: appsWin; imagesWin: imagesWin; codeWin: codeWin; fixItWin: fixItWin
                            overview: overview; moveInWin: moveInWin; firstRun: firstRun; describer: describer
                            onOpened: root.collapse()
                            onOpenRole: role => root.openApp(role)
                            // The Guardian approval dialog, where the desktop has one (Guardian.qml).
                            onReview: card => {
                                try { guardianWin.open(card.plan ? card.plan.id : card.id, card.plan); actionCards.reviewTaken = true }
                                catch (e) { actionCards.reviewTaken = false }
                            }
                        }
                        Flow {
                            width: parent.width; spacing: 6
                            visible: !root.error && root.provider !== ""
                            Repeater {
                                model: ["Answer · Nexus Core (" + root.provider + " provider)"].concat(
                                    root.suggestedBy ? ["Apps · " + ({ rules: "keyword rules, not AI", model: "chosen by the model",
                                                                     "rules-fallback": "keyword rules (model unavailable)" })[root.suggestedBy]] : [])
                                delegate: Rectangle {
                                    required property string modelData
                                    width: chip.implicitWidth + 20; height: 28; radius: 14
                                    color: root.dark.chip
                                    Text { id: chip; anchors.centerIn: parent; text: modelData; font.pixelSize: 13; color: root.dark.inkSecondary }
                                }
                            }
                        }
                    }
                    // Scroll position, shown only when there is more than fits.
                    Rectangle {
                        visible: replyView.contentHeight > replyView.height
                        x: replyView.width - 4; width: 3; radius: 1.5
                        y: replyView.contentY + replyView.visibleArea.yPosition * replyView.height
                        height: Math.max(24, replyView.visibleArea.heightRatio * replyView.height)
                        color: root.dark.glassBorder
                    }
                }
            }
        }
    }
}
