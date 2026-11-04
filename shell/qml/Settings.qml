import QtQuick
import QtQuick.Shapes
import Quickshell
import Quickshell.Io
import Quickshell.Hyprland
import Quickshell.Services.Pipewire
import "settings"

// Settings (design: "Settings B · Orbital categories", approved with the AI
// changes window). The categories orbit on the left, the chosen page fills
// the right, and the box at the top searches every setting; if nothing
// matches, or the person picks "Ask Nexus", it asks Nexus in place
// (settings/AskBox.qml, "Settings C · Ask"). Opened from the orbital controls,
// the top bar, the Nexus orbit, Super+I, or `nexus-shell-ipc call settings open <page>`.
// Every page reads real state and changes it through the service that owns
// it (PipeWire, NetworkManager, BlueZ, UPower, Hyprland). System-wide changes
// go through Guardian, never from here.
FloatingWindow {
    id: win
    property var theme
    property var bridge
    property var desktop
    property bool reduced: false
    property var cardHost: null     // the shell's ActionCards, for "Do it" cards in the ask box
    signal askNexus(string text)
    signal fixIt(string problem)
    signal openWelcome()
    signal moveIn()

    title: "Settings"
    visible: false
    implicitWidth: 1120; implicitHeight: 740
    minimumSize: Qt.size(920, 620)
    color: "#05071A"

    property string page: "network"
    property string highlight: ""
    Timer { id: unhighlight; interval: 2600; onTriggered: win.highlight = "" }
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"

    readonly property var pages: [
        { id: "network", label: "Network", colour: "#86A8FF", icon: "network" },
        { id: "bluetooth", label: "Bluetooth", colour: "#86A8FF", icon: "bluetooth" },
        { id: "sound", label: "Sound", colour: "#9C8FFF", icon: "sound" },
        { id: "display", label: "Display", colour: "#FFA65C", icon: "display" },
        { id: "power", label: "Power", colour: "#7FE0B0", icon: "power" },
        { id: "desktop", label: "Desktop", colour: "#9C8FFF", icon: "desktop" },
        { id: "privacy", label: "Privacy and AI", colour: "#9C8FFF", icon: "privacy" },
        { id: "search", label: "Search", colour: "#86A8FF", icon: "search" },
        { id: "timeline", label: "Timeline", colour: "#FFA65C", icon: "time" },
        { id: "automations", label: "Automations", colour: "#7FE0B0", icon: "spark" },
        { id: "accessibility", label: "Accessibility", colour: "#86A8FF", icon: "accessibility" },
        { id: "health", label: "Health", colour: "#7FE0B0", icon: "health" },
        { id: "about", label: "About", colour: "#9AA9C9", icon: "about" }
    ]
    // What the search box finds: [page, highlight id, label, words people might type].
    readonly property var index: [
        ["network", "wifi", "Wi-Fi", "wifi wireless internet network password connect hotspot"],
        ["network", "wired", "Wired connection", "ethernet cable lan wired"],
        ["network", "advanced", "VPN and work networks", "vpn eduroam enterprise certificate nmtui proxy"],
        ["bluetooth", "bluetooth", "Bluetooth", "bluetooth headphones earbuds mouse keyboard pair speaker"],
        ["sound", "volume", "Volume", "volume sound audio speaker loud quiet mute"],
        ["sound", "output", "Output device", "headphones speakers output hdmi device"],
        ["sound", "microphone", "Microphone", "microphone mic input record"],
        ["display", "brightness", "Brightness", "brightness dim bright screen backlight"],
        ["display", "nightlight", "Night light", "night light warm blue light evening eyes colour temperature"],
        ["display", "scale", "Scale", "scale zoom size bigger text resolution dpi"],
        ["power", "battery", "Battery", "battery charge charging time left"],
        ["power", "powermode", "Power mode", "power mode performance saver balanced fan"],
        ["power", "session", "Lock, log out, restart, shut down", "lock log out logout restart reboot shut down shutdown power off sleep"],
        ["desktop", "windows", "Floating or tiled windows", "windows floating tiling tiled tile layout"],
        ["desktop", "blur", "Blur", "blur glass transparency effects"],
        ["desktop", "animations", "Animations", "animations motion effects reduce"],
        ["desktop", "shortcuts", "Keyboard shortcuts", "keyboard shortcuts keys hotkeys super"],
        ["privacy", "model", "AI model", "model ai ollama qwen llm assistant nexus"],
        ["privacy", "provider", "Cloud or local answers", "cloud local openai provider privacy online"],
        ["privacy", "notes", "Your notes for Nexus", "notes training documents teach nexus about me instructions"],
        ["privacy", "history", "Conversation history", "history conversations erase delete forget"],
        ["privacy", "guardian", "System changes", "guardian system change install ssh approval"],
        ["privacy", "forget", "Forget today", "forget today privacy dashboard what nexus read left the computer microphone camera"],
        ["search", "", "Search", "search find files folders index private documents choose what is searched"],
        ["timeline", "", "Timeline", "timeline history undo changes what changed recent"],
        ["automations", "", "Automations", "automations rules when if then automatic schedule routine trigger"],
        ["accessibility", "textsize", "Bigger text", "accessibility bigger text larger font zoom vision"],
        ["accessibility", "contrast", "High contrast", "high contrast accessibility vision"],
        ["accessibility", "reader", "Screen reader", "screen reader orca blind accessibility read aloud"],
        ["accessibility", "pointer", "Pointer size", "pointer cursor mouse size bigger"],
        ["accessibility", "motion", "Less motion", "reduce motion animations dizzy accessibility"],
        ["health", "", "Health", "health report problems disk smart updates check status"],
        ["about", "name", "Computer name", "about computer name hostname version"],
        ["about", "storage", "Storage", "storage disk space full free files"],
        ["about", "welcome", "Welcome setup", "welcome first run setup bundles start again apps for studying gaming office coding"],
        ["about", "movein", "Bring files from Windows or macOS", "move in old computer windows mac macos transfer copy files documents photos drive"],
        ["about", "generations", "Earlier versions", "rollback undo update generation boot earlier version"]
    ]
    function search(text) {
        const q = text.trim().toLowerCase();
        if (!q) return [];
        const words = q.split(/\s+/);
        return index.map(([p, h, label, keys]) => {
            const hay = (label + " " + keys).toLowerCase();
            const score = words.filter(w => hay.includes(w)).length;
            return { page: p, highlight: h, label: label, score: score };
        }).filter(r => r.score > 0).sort((a, b) => b.score - a.score).slice(0, 6);
    }

    function open(pageId, highlightId) {
        if (pages.some(p => p.id === pageId)) page = pageId;
        highlight = highlightId || "";
        if (highlight) unhighlight.restart();
        if (visible) hypr(["dispatch", "focuswindow", "title:^(Settings)$"]);
        visible = true;
    }
    function hypr(args) { Quickshell.execDetached([hyprctl].concat(args)) }
    // Undo from the Timeline: Core hands back the old value and where it belongs.
    function restore(ref, value) {
        if (typeof ref === "string" && ref.startsWith("desktop:") && value && "value" in value) desktop.set(ref.slice(8), value.value, true);
        // The volume, when Nexus changed it (ActionCards.qml).
        else if (ref === "sound:volume" && value && typeof value.value === "number" && Pipewire.defaultAudioSink && Pipewire.defaultAudioSink.audio)
            Pipewire.defaultAudioSink.audio.volume = Math.max(0, Math.min(1, value.value / 100));
    }
    function lock() { Quickshell.execDetached((Quickshell.env("NEXUS_LOCK") || "hyprlock").split(" ")) }
    function runInTerminal(command) { Quickshell.execDetached([Quickshell.env("NEXUS_TERMINAL") || "kitty"].concat(command)) }
    function openFolder(path) {
        Quickshell.execDetached(["mkdir", "-p", path]);
        Quickshell.execDetached(["xdg-open", path]);
    }

    readonly property WifiModel wifi: WifiModel {}

    IpcHandler {
        target: "settings"
        function open(page: string): void { win.open(page, "") }
        function close(): void { win.visible = false }
        function state(): string { return JSON.stringify({ visible: win.visible, page: win.page }) }
        // Types into the search box, as a person would (for tests and previews).
        function search(text: string): string { query.text = text; return JSON.stringify(win.search(text)) }
        // Asks in the Settings ask box, as if typed there.
        function ask(text: string): void { win.open(win.page, ""); askBox.show(text) }
        // Wi-Fi for tests: rows with their centres in window coordinates.
        function wifi(): string {
            const rows = win.visible && win.page === "network" && pageLoader.item ? pageLoader.item.ipcRows() : [];
            return JSON.stringify({ visible: win.visible, page: win.page, wifiOn: win.wifi.on, passwordFor: win.wifi.passwordFor,
                                    error: win.wifi.error, note: win.wifi.note, rows: rows });
        }
    }

    // ---- the window ----
    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#0B0F24" }
            GradientStop { position: 0.6; color: "#05071A" }
            GradientStop { position: 1; color: "#020309" }
        }
    }
    Canvas {
        anchors.fill: parent
        onWidthChanged: requestPaint()
        onHeightChanged: requestPaint()
        onPaint: {
            const ctx = getContext("2d");
            ctx.reset();
            let seed = 41;
            const rnd = () => { seed = (seed * 1664525 + 1013904223) % 4294967296; return seed / 4294967296 };
            for (let i = 0; i < 80; i++) {
                ctx.fillStyle = "rgba(201,212,255," + (0.25 + rnd() * 0.6).toFixed(2) + ")";
                const s = rnd() < 0.85 ? 1 : 2;
                ctx.fillRect(Math.round(rnd() * width), Math.round(rnd() * height), s, s);
            }
        }
    }

    Item {
        id: left
        x: 0; y: 0; width: Math.min(470, win.width * 0.42); height: parent.height

        Column {
            x: 36; y: 28; spacing: 4
            Text { text: "Settings"; color: win.theme.ink; font.pixelSize: 26; font.weight: 600 }
            Text { text: "Pick a planet, or type to search"; color: win.theme.inkSecondary; font.pixelSize: 13 }
        }

        // Search, with the results under it.
        Rectangle {
            id: searchBox
            x: 36; y: 96; width: left.width - 72; height: 46
            radius: 14; color: win.theme.glass
            border.color: query.activeFocus ? win.theme.focus : win.theme.glassBorder
            Image { x: 14; anchors.verticalCenter: parent.verticalCenter; width: 18; height: 18
                    sourceSize: Qt.size(36, 36); source: "icons/search.svg" }
            TextInput {
                id: query
                x: 42; width: parent.width - 56; anchors.verticalCenter: parent.verticalCenter
                color: win.theme.ink; font.pixelSize: 14; clip: true
                Text { visible: !query.text; text: "Search or ask, e.g. make text bigger"; color: win.theme.inkSecondary; font.pixelSize: 14 }
                Keys.onEscapePressed: text = ""
                onAccepted: {
                    const r = win.search(text);
                    if (r.length) { win.open(r[0].page, r[0].highlight); text = "" }
                    else if (text.trim()) { askBox.show(text.trim()); text = "" }
                }
                Accessible.role: Accessible.EditableText
                Accessible.name: "Search settings"
            }
            MouseArea { anchors.fill: query; cursorShape: Qt.IBeamCursor; onClicked: query.forceActiveFocus() }
        }
        Rectangle {
            id: results
            readonly property var found: win.search(query.text)
            visible: query.text.trim() !== ""
            z: 2
            x: searchBox.x; y: searchBox.y + searchBox.height + 6; width: searchBox.width
            height: resultColumn.implicitHeight + 12
            radius: 14; color: "#0B1430"; border.color: win.theme.glassBorder
            Column {
                id: resultColumn
                x: 6; y: 6; width: parent.width - 12
                Repeater {
                    model: results.found
                    delegate: Rectangle {
                        required property var modelData
                        width: resultColumn.width; height: 38; radius: 9
                        color: rh.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.2) : "transparent"
                        Text { x: 12; anchors.verticalCenter: parent.verticalCenter; text: modelData.label; color: win.theme.ink; font.pixelSize: 13 }
                        Text { anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
                               text: win.pages.find(p => p.id === modelData.page).label; color: win.theme.inkSecondary; font.pixelSize: 12 }
                        HoverHandler { id: rh; cursorShape: Qt.PointingHandCursor }
                        TapHandler { onTapped: { win.open(modelData.page, modelData.highlight); query.text = "" } }
                        Accessible.role: Accessible.Button
                        Accessible.name: modelData.label
                    }
                }
                // Nothing matched, or it reads like a question: Nexus can answer it.
                Rectangle {
                    width: resultColumn.width; height: 38; radius: 9
                    color: ah.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.2) : "transparent"
                    Text { x: 12; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 24; elide: Text.ElideRight
                           text: "Ask Nexus: “" + query.text.trim() + "”"; textFormat: Text.PlainText
                           color: win.theme.focus; font.pixelSize: 13 }
                    HoverHandler { id: ah; cursorShape: Qt.PointingHandCursor }
                    TapHandler { onTapped: { askBox.show(query.text.trim()); query.text = "" } }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Ask Nexus"
                }
            }
        }

        // The orbit of categories.
        Item {
            id: orbit
            readonly property real cx: left.width / 2
            readonly property real cy: 160 + (left.height - 160) / 2
            readonly property real r: Math.min(left.width / 2 - 70, (left.height - 160) / 2 - 56)
            anchors.fill: parent
            Shape {
                x: orbit.cx; y: orbit.cy
                preferredRendererType: Shape.CurveRenderer
                ShapePath {
                    strokeColor: Qt.rgba(0.55, 0.62, 1.0, 0.22); strokeWidth: 1; fillColor: "transparent"
                    PathAngleArc { radiusX: orbit.r; radiusY: radiusX; sweepAngle: 360 }
                }
                ShapePath {
                    strokeColor: Qt.rgba(0.55, 0.62, 1.0, 0.14); strokeWidth: 1; fillColor: "transparent"
                    strokeStyle: ShapePath.DashLine; dashPattern: [2, 7]
                    PathAngleArc { radiusX: orbit.r * 0.55; radiusY: radiusX; sweepAngle: 360 }
                }
            }
            // The chosen category, at the centre.
            Column {
                id: centre
                x: orbit.cx - width / 2; y: orbit.cy - height / 2
                spacing: 8
                readonly property var current: win.pages.find(p => p.id === win.page)
                Rectangle {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 72; height: 72; radius: 36
                    gradient: Gradient {
                        GradientStop { position: 0; color: centre.current.colour }
                        GradientStop { position: 0.85; color: "#121B33" }
                    }
                    border.color: win.theme.focus
                    Image { anchors.centerIn: parent; width: 30; height: 30; sourceSize: Qt.size(60, 60)
                            source: "icons/" + centre.current.icon + ".svg" }
                }
                Text { anchors.horizontalCenter: parent.horizontalCenter; text: centre.current.label
                       color: win.theme.ink; font.pixelSize: 14; font.weight: 600 }
            }
            Repeater {
                model: win.pages
                delegate: Item {
                    id: planet
                    required property var modelData
                    required property int index
                    readonly property bool current: modelData.id === win.page
                    readonly property bool matched: results.visible && results.found.some(r => r.page === modelData.id)
                    readonly property real angle: -Math.PI / 2 + index * Math.PI * 2 / win.pages.length
                    readonly property real size: current ? 48 : 36
                    x: orbit.cx + orbit.r * Math.cos(angle); y: orbit.cy + orbit.r * Math.sin(angle)
                    opacity: results.visible && !matched ? 0.4 : 1
                    Rectangle {
                        x: -width / 2; y: -height / 2
                        width: planet.size; height: width; radius: width / 2
                        Behavior on width { NumberAnimation { duration: win.reduced ? 0 : 180 } }
                        gradient: Gradient {
                            GradientStop { position: 0; color: planet.modelData.colour }
                            GradientStop { position: 0.8; color: "#121B33" }
                        }
                        border.color: planet.current || ph.hovered || planet.activeFocus || planet.matched ? win.theme.focus : win.theme.glassBorder
                        border.width: planet.activeFocus ? 3 : 1
                        Image { anchors.centerIn: parent; width: parent.width * 0.45; height: width; sourceSize: Qt.size(48, 48)
                                source: "icons/" + planet.modelData.icon + ".svg" }
                    }
                    Text {
                        y: planet.size / 2 + 6; x: -width / 2
                        text: planet.modelData.label
                        color: planet.current ? win.theme.ink : win.theme.inkSecondary; font.pixelSize: 12
                    }
                    Item {
                        x: -30; y: -30; width: 60; height: 72
                        HoverHandler { id: ph; cursorShape: Qt.PointingHandCursor }
                        TapHandler { onTapped: win.open(planet.modelData.id, "") }
                    }
                    activeFocusOnTab: true
                    Keys.onReturnPressed: win.open(modelData.id, "")
                    Keys.onSpacePressed: win.open(modelData.id, "")
                    Accessible.role: Accessible.PageTab
                    Accessible.name: modelData.label
                    Accessible.selected: current
                }
            }
        }
    }

    // The chosen page.
    Rectangle {
        id: pageCard
        x: left.width; y: 24; width: parent.width - left.width - 24; height: parent.height - 48
        radius: 22; color: win.theme.glass; border.color: win.theme.glassBorder
        Flickable {
            id: scroller
            anchors { fill: parent; margins: 28 }
            contentWidth: width; contentHeight: pageLoader.item ? pageLoader.item.implicitHeight + 8 : 0
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            // Pages are created with their theme and host already set, and only
            // while the window is open (a closed Settings scans nothing).
            Loader {
                id: pageLoader
                width: scroller.width
                readonly property string file: "settings/" + ({ network: "NetworkPage", bluetooth: "BluetoothPage", sound: "SoundPage",
                    display: "DisplayPage", power: "PowerPage", desktop: "DesktopPage", privacy: "PrivacyPage", about: "AboutPage",
                    timeline: "TimelinePage", automations: "AutomationsPage", accessibility: "AccessibilityPage", health: "HealthPage",
                    search: "SearchPage" })[win.page] + ".qml"
                function reload() {
                    if (win.visible) setSource(file, { theme: win.theme, host: win });
                    else source = "";
                    scroller.contentY = 0;
                }
                onFileChanged: reload()
                Connections { target: win; function onVisibleChanged() { pageLoader.reload() } }
            }
        }
    }

    AskBox {
        id: askBox
        z: 5
        x: 24; y: 90; width: left.width - 32; maxHeight: win.height - 114
        theme: win.theme; bridge: win.bridge; host: win; cardHost: win.cardHost
    }
}
