// Nexus shell: the SpaceUI desktop for Hyprland, in Quickshell (ADR-003).
// It draws the wallpaper, the bottom Nexus launcher, the full Nexus core with
// its app orbit and chat, and the system panel. Hyprland manages the windows.
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
    property int suggestId: -1
    property real voice: 0
    // GPU budget (the local model shares the GPU with the core):
    // - while the model is generating, the big core caps its frame rate and detail;
    // - the launcher core stops drawing while the screen is idle or a window is fullscreen.
    // simulateBusy lets the benchmark hold the busy state without a chat.
    property bool simulateBusy: false
    readonly property bool modelBusy: simulateBusy || mode === "thinking" || mode === "responding"
    readonly property bool fullscreenActive: ToplevelManager.activeToplevel ? ToplevelManager.activeToplevel.fullscreen : false
    readonly property bool launcherPaused: idle.isIdle || fullscreenActive
    // Provisional dark theme (approved 22 Oct): deep space, light ink, glass surfaces.
    readonly property var dark: ({
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
        input.text = ""; reply = ""; error = ""; mode = "idle"; voice = 0;
    }
    function ask(prompt) {
        if (!bridge.online) return;
        error = ""; reply = ""; provider = ""; mode = "thinking";
        lastPrompt = prompt;
        chatId = bridge.chat(prompt);
    }
    function openApp(id) {
        if (apps.open(id)) collapse();
    }

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
        onDone: (id, provider) => {
            if (id !== root.chatId) return;
            root.provider = provider;
            settle.restart();
            // With the answer done the model is free: let it pick the apps,
            // falling back to the rules (and saying so) if it cannot.
            root.suggestId = bridge.suggest(root.lastPrompt, true);
        }
        onFailed: (id, message) => {
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
    Wallpaper {}

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
    IpcHandler {
        target: "nexus"
        function toggle(): void { root.expanded ? root.collapse() : root.open() }
        function state(): string {
            return JSON.stringify({ expanded: root.expanded, online: bridge.online, mode: root.mode, orbit: root.visibleApps(),
                                    modelBusy: root.modelBusy, launcherPaused: root.launcherPaused })
        }
        function simulateBusy(on: bool): void { root.simulateBusy = on }
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
        implicitWidth: root.lay.surface.width
        implicitHeight: root.lay.surface.height
        color: "transparent"
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.layer: WlrLayer.Top            // below lock and secure surfaces
        WlrLayershell.namespace: "nexus-spaceui"
        // While Nexus is open it takes the keyboard, like a launcher: typing goes
        // straight to the chat; Escape or a click outside gives it back.
        WlrLayershell.keyboardFocus: root.expanded ? WlrKeyboardFocus.Exclusive : WlrKeyboardFocus.None
        // Only the drawn parts take input; the rest of the surface passes clicks through.
        mask: Region { item: root.expanded ? fullHit : launcherHit }

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
                        TapHandler { enabled: launcher.hoverOpen; onTapped: modelData.activate() }
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
                    readonly property bool available: apps.installed(modelData)
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
                    x: 58; width: parent.width - 130; anchors.verticalCenter: parent.verticalCenter
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
                    Keys.onEscapePressed: root.collapse()
                    Accessible.name: "Ask Nexus AI"
                }
                MouseArea { anchors.fill: input; onClicked: input.forceActiveFocus(); cursorShape: Qt.IBeamCursor }
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

            // Reply, with its sources kept visibly separate.
            Column {
                x: root.lay.reply.x + 12; y: root.lay.reply.y
                width: root.lay.reply.width - 24
                spacing: 8
                visible: root.reply.length > 0 || root.error.length > 0
                Text {
                    width: parent.width; wrapMode: Text.Wrap
                    textFormat: Text.PlainText      // model output is never rendered as rich text
                    maximumLineCount: 6; elide: Text.ElideRight
                    text: root.error ? "Nexus Core could not answer: " + root.error : root.reply
                    font.pixelSize: 14; lineHeight: 1.5; color: root.error ? root.dark.error : root.dark.ink
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
        }
    }

    SystemPanel { theme: root.dark; reduced: root.reduced }
}
