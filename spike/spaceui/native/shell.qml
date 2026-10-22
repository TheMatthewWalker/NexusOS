// SpaceUI spike A: a native shell in Quickshell (QML on Qt 6).
// Run: quickshell -p spike/spaceui/native/shell.qml   (see scripts/spike/harness.py)
import QtQuick
import QtQuick.Shapes
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import "shared/suggest.mjs" as S
import "shared/tokens.mjs" as T

ShellRoot {
    id: root
    readonly property var tk: T.tokens.baseline
    readonly property var lay: T.layout
    readonly property bool reduced: Quickshell.env("NEXUS_REDUCED_MOTION") === "1"
    property bool expanded: false
    property bool showAll: false
    property var suggested: []
    property var replyApps: []
    property string mode: "idle"            // idle | typing | thinking | responding
    property string reply: ""
    property string shownReply: ""
    property string provider: ""
    property string error: ""
    property int frames: 0
    property real voice: 0
    // Provisional dark theme (approved 22 Oct): deep space, light ink, glass surfaces.
    readonly property var dark: ({
        ink: "#E3EAFF", inkSecondary: "#9AA9C9", glass: Qt.rgba(0.04, 0.07, 0.15, 0.80),
        glassBorder: "#2A3A66", focus: "#86A8FF", primary: "#7C6BFF", chip: "#121B33",
        orbit: Qt.rgba(0.55, 0.62, 1.0, 0.22), error: "#FF8FA3"
    })

    function visibleApps() {
        if (showAll) return S.APP_IDS;
        const all = suggested.concat(replyApps);
        return S.APP_IDS.filter(id => all.includes(id));
    }
    function collapse() {
        expanded = false; showAll = false; suggested = []; replyApps = [];
        input.text = ""; reply = ""; shownReply = ""; error = ""; mode = "idle";
    }
    function ask(prompt) {
        error = ""; reply = ""; shownReply = ""; mode = "thinking";
        askProc.command = [Quickshell.env("NEXUS_CLI"), "ask", prompt];
        askProc.running = true;
    }
    function openApp(id) {
        Quickshell.execDetached([Quickshell.env("SPIKE_LAUNCH"), id]);
        collapse();
    }

    // Frame counter: the core's FrameAnimation ticks once per presented frame.
    Timer {
        interval: 1000; running: true; repeat: true
        onTriggered: { console.log("SPIKE t=" + (Date.now() / 1000).toFixed(2) + " fps=" + root.frames); root.frames = 0 }
    }

    Process {
        id: askProc
        stdout: StdioCollector { id: answer }
        stderr: StdioCollector { id: askErr }
        onExited: (code) => {
            try {
                if (code !== 0) throw new Error(askErr.text || "Nexus Core did not answer");
                const r = JSON.parse(answer.text);
                root.provider = r.provider; root.reply = r.answer;
                root.replyApps = S.suggestApps(input.text);
                root.mode = "responding";
                typer.start();
            } catch (e) {
                root.error = String(e.message || e).trim(); root.mode = "idle";
            }
        }
    }
    // Demo pacing from the design pack (2 characters every 22 ms): Core does
    // not stream yet, so the reply is revealed locally.
    Timer {
        id: typer
        interval: root.reduced ? 1 : root.tk.demoOnlyTiming.characterTick; repeat: true
        onTriggered: {
            root.shownReply = root.reduced ? root.reply : root.reply.slice(0, root.shownReply.length + 2);
            // The core "speaks" the reply: vowels peak, consonants half, gaps rest.
            const ch = root.shownReply.slice(-1);
            root.voice = /[aeiouy]/i.test(ch) ? 1 : /[a-z0-9]/i.test(ch) ? 0.55 : 0.12;
            if (root.shownReply.length >= root.reply.length) { stop(); root.voice = 0; settle.start() }
        }
    }
    Timer { id: settle; interval: 1200; onTriggered: root.mode = "idle" }
    Timer { id: debounce; interval: root.tk.appOrbit.motion.intentDebounce; onTriggered: root.suggested = S.suggestApps(input.text) }

    PanelWindow {
        id: panel
        anchors.bottom: true
        implicitWidth: root.lay.surface.width
        implicitHeight: root.lay.surface.height
        color: "transparent"
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.layer: WlrLayer.Top            // below lock and secure surfaces
        WlrLayershell.namespace: "nexus-spaceui"
        WlrLayershell.keyboardFocus: root.expanded ? WlrKeyboardFocus.OnDemand : WlrKeyboardFocus.None
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

        // ---- collapsed: the bottom launcher with open apps as tiny planets ----
        Item {
            id: launcher
            visible: !root.expanded
            x: root.lay.launcher.x; y: root.lay.launcher.y
            property bool hoverOpen: false
            readonly property var apps: ToplevelManager.toplevels.values

            Timer { id: hoverDelay; interval: root.lay.launcherHoverDelay; onTriggered: launcher.hoverOpen = true }
            // The hover zone tracks the input mask, so the widened orbit stays open
            // while the pointer moves out to a planet.
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
                reduced: root.reduced
                hovered: launcherHover.hovered
                onFrame: root.frames++
            }
            Item {
                width: root.lay.launcher.diameter; height: width
                x: -width / 2; y: -height / 2
                TapHandler { onTapped: root.expanded = true }
                Accessible.role: Accessible.Button
                Accessible.name: "Open Nexus"
            }
            Repeater {
                model: launcher.apps
                delegate: Item {
                    required property var modelData
                    required property int index
                    readonly property var p: S.launcherPlanet(index, launcher.apps.length, launcher.hoverOpen)
                    x: p.x; y: p.y
                    Behavior on x { NumberAnimation { duration: root.reduced ? 0 : 260; easing.type: Easing.OutCubic } }
                    Behavior on y { NumberAnimation { duration: root.reduced ? 0 : 260; easing.type: Easing.OutCubic } }
                    Rectangle {
                        width: p.size; height: p.size; radius: width / 2
                        x: -width / 2; y: -height / 2
                        color: root.dark.glass; border.color: root.dark.glassBorder; border.width: 1
                        Behavior on width { NumberAnimation { duration: root.reduced ? 0 : 260 } }
                        Image {
                            anchors.centerIn: parent
                            width: parent.width * 0.55; height: width
                            visible: launcher.hoverOpen
                            source: "shared/icons-dark/" + (modelData.appId.replace("nexus-spike-", "") || "files") + ".svg"
                            sourceSize: Qt.size(48, 48)
                        }
                        TapHandler { enabled: launcher.hoverOpen; onTapped: modelData.activate() }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Switch to " + modelData.title
                    }
                    Text {
                        visible: launcher.hoverOpen
                        y: p.size / 2 + 4; x: -width / 2
                        text: modelData.title.replace(" (spike stand-in)", "")
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

            Shape {
                x: root.lay.core.x; y: root.lay.core.y
                preferredRendererType: Shape.CurveRenderer
                ShapePath {
                    strokeColor: root.dark.orbit
                    strokeWidth: 1; fillColor: "transparent"
                    PathAngleArc { radiusX: root.tk.appOrbit.radius; radiusY: radiusX; sweepAngle: 360 }
                }
                ShapePath {
                    strokeColor: Qt.rgba(0.55, 0.62, 1.0, 0.16); strokeWidth: 1; fillColor: "transparent"
                    strokeStyle: ShapePath.DashLine; dashPattern: [2, 7]
                    PathAngleArc { radiusX: root.tk.appOrbit.innerRadius; radiusY: radiusX; sweepAngle: 360 }
                }
            }

            NovaCore {
                id: bigCore
                anchors.fill: parent
                z: -1
                centre: Qt.point(root.lay.core.x, root.lay.core.y)
                radius: root.tk.nexus.hitDiameter / 2
                reduced: root.reduced
                mode: ({ idle: "idle", typing: "listening", thinking: "thinking", responding: "speaking" })[root.mode]
                voice: root.voice
                hovered: coreHover.hovered
                onFrame: if (root.expanded) root.frames++
            }
            // Touching the core: hover wakes it, a tap brings every app into orbit, a drag turns it.
            Item {
                width: root.tk.nexus.hitDiameter; height: width
                x: root.lay.core.x - width / 2; y: root.lay.core.y - height / 2
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
                Text { anchors.horizontalCenter: parent.horizontalCenter
                       text: ({ idle: "Ready when you are", typing: "Listening", thinking: "Asking Nexus Core…", responding: "Responding" })[root.mode]
                       font.pixelSize: 13; color: root.dark.inkSecondary }
            }

            Repeater {
                model: S.APP_IDS
                delegate: Item {
                    id: node
                    required property string modelData
                    required property int index
                    readonly property bool shown: root.expanded && root.visibleApps().includes(modelData)
                    property real progress: 0
                    readonly property real eased: shown ? S.entryEase(progress) : 1 - S.exitEase(1 - progress)
                    readonly property var at: root.reduced ? Object.assign(S.slot(modelData), { scale: 1, opacity: progress })
                                                           : S.emergence(modelData, eased)
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
                        border.color: btnHover.hovered && node.settled ? root.dark.focus : root.dark.glassBorder; border.width: 1.5
                        Image { anchors.centerIn: parent; width: 30; height: 30; sourceSize: Qt.size(60, 60)
                                source: "shared/icons-dark/" + node.modelData + ".svg" }
                        HoverHandler { id: btnHover; enabled: node.settled }
                        TapHandler { enabled: node.settled; onTapped: root.openApp(node.modelData) }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Open " + S.APP_LABELS[node.modelData]
                    }
                    Text {
                        y: root.tk.appOrbit.buttonDiameter / 2 + root.tk.appOrbit.labelGap; x: -width / 2
                        text: S.APP_LABELS[node.modelData]; font.pixelSize: 13; font.weight: 500
                        color: root.dark.ink
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
                        source: "shared/icons/spark.svg"; sourceSize: Qt.size(44, 44) }
                TextInput {
                    id: input
                    x: 58; width: parent.width - 130; anchors.verticalCenter: parent.verticalCenter
                    font.pixelSize: 14; color: root.dark.ink; clip: true
                    Text { visible: !input.text && !input.activeFocus; text: "Ask Nexus AI or find anything"
                           font.pixelSize: 14; color: root.dark.inkSecondary }
                    onTextEdited: { root.mode = text ? "typing" : "idle"; debounce.restart() }
                    onAccepted: if (text.trim()) root.ask(text.trim())
                    Keys.onEscapePressed: root.collapse()
                    Accessible.name: "Ask Nexus AI"
                }
                MouseArea { anchors.fill: input; onClicked: input.forceActiveFocus(); cursorShape: Qt.IBeamCursor }
                Rectangle {
                    width: root.tk.chat.sendDiameter; height: width; radius: width / 2
                    anchors.right: parent.right; anchors.rightMargin: 10; anchors.verticalCenter: parent.verticalCenter
                    color: root.dark.primary
                    Image { anchors.centerIn: parent; width: 20; height: 20; source: "shared/icons/send.svg"; sourceSize: Qt.size(40, 40) }
                    TapHandler { onTapped: if (input.text.trim()) root.ask(input.text.trim()) }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Send"
                }
            }

            // Reply, with its sources kept visibly separate.
            Column {
                x: root.lay.reply.x + 12; y: root.lay.reply.y
                width: root.lay.reply.width - 24
                spacing: 8
                visible: root.shownReply.length > 0 || root.error.length > 0
                Text {
                    width: parent.width; wrapMode: Text.Wrap
                    textFormat: Text.PlainText      // model output is never rendered as rich text
                    text: root.error ? "Nexus Core could not answer: " + root.error : root.shownReply
                    font.pixelSize: 14; lineHeight: 1.5; color: root.error ? root.dark.error : root.dark.ink
                }
                Flow {
                    width: parent.width; spacing: 6
                    visible: !root.error && root.shownReply.length >= root.reply.length
                    Repeater {
                        model: ["Answer · Nexus Core (" + root.provider + " provider)",
                                "Apps · keyword match, not AI (prototype)"]
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
}
