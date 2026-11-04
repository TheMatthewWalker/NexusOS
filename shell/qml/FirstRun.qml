import QtQuick
import QtQuick.Shapes
import Quickshell
import Quickshell.Io
import "settings"
import "lib/bundles.mjs" as BundleData
import "lib/firstrun.mjs" as F

// First-run setup (feature 4, design "Feat-FirstRun"): after the first login,
// "What will you mostly use this computer for?" Chips (or the person's own
// words, matched to keywords) pick curated bundles (nix/bundles/*.json); the
// person removes anything they do not want, then:
//  - apps install from Flathub for this person only (flatpak --user, as Apps does);
//  - personal settings are applied through DesktopSettings (journalled, so
//    Timeline can undo them);
//  - system items go to Guardian as one typed plan (/guardian/preview), which
//    waits for the person's approval; nothing system-wide changes here.
// Only bundle items can appear: free text picks bundles, never packages.
// Shown once (marker ~/.config/nexusos/first-run-done) when the desktop sets
// NEXUS_FIRST_RUN=1; reopened from Settings, About, or `nexus-shell-ipc call firstrun open`.
NexusWindow {
    id: win
    property var theme
    property var bridge
    property var desktop
    signal moveIn()
    // The system items as one plan, for the approval dialog (Core's /guardian/preview answer).
    signal proposeSystem(var plan)
    title: "Welcome"
    visible: false
    implicitWidth: 1100; implicitHeight: 780
    minimumSize: Qt.size(860, 640)
    color: "#05071A"

    readonly property var bundles: BundleData.bundles
    readonly property string configDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos"
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    property string step: "choose"          // choose | setup
    property var chosen: []
    property var removed: []
    property var matchedWords: []
    readonly property var plan: F.merge(chosen, bundles, removed)
    readonly property var labels: bundles.filter(b => chosen.includes(b.id)).map(b => b.label)
    property var installedIds: []
    // Setting up: one status per item key ("waiting", "working", "done", "failed", "had", "approval").
    property var progress: ({})
    property var errors: ({})               // item key -> flatpak's last message, when an install failed
    property var queue: []
    property string current: ""
    property string systemNote: ""
    property int previewId: -1

    function open() {
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Welcome)$"]);
        if (step === "setup" && !busy()) { step = "choose"; progress = ({}); errors = ({}) }
        visible = true;
        listInstalled.running = true;
    }
    function toggle(id) {
        if (!bundles.some(b => b.id === id)) return false;
        chosen = chosen.includes(id) ? chosen.filter(x => x !== id) : bundles.map(b => b.id).filter(x => x === id || chosen.includes(x));
        return true;
    }
    function describe(text) {
        const m = F.match(text, bundles);
        matchedWords = m.words;
        if (m.ids.length) chosen = bundles.map(b => b.id).filter(x => m.ids.includes(x) || chosen.includes(x));
        return m.ids;
    }
    function remove(key) { if (!removed.includes(key)) removed = removed.concat([key]) }
    function busy() { return current !== "" || queue.length > 0 }
    function setStatus(key, status) { const p = Object.assign({}, progress); p[key] = status; progress = p }

    function finish() {
        // Shown once: the marker says the welcome has been seen (skipped or set up).
        marker.running = true;
    }
    function skip() { finish(); visible = false }
    function apply() {
        if (busy() || chosen.length === 0) return false;
        step = "setup"; progress = ({}); systemNote = "";
        // Personal settings: at once, through the same path as Settings.
        for (const s of plan.settings) { desktop.set(s.key, s.value); setStatus(s.id, "done") }
        // System items: one plan, waiting for approval.
        const sys = F.systemPlan(plan.system, labels);
        if (sys) {
            for (const i of plan.system) setStatus(i.id, "waiting");
            if (bridge.online) previewId = bridge.api("POST", "/guardian/preview", sys);
            else { systemNote = "Nexus Core is not running, so the system items could not be prepared. Try again from Settings, About."; for (const i of plan.system) setStatus(i.id, "failed") }
        }
        // Apps: one after another, for this person only.
        queue = plan.apps.filter(a => { if (installedIds.includes(a.flathub)) { setStatus(a.id, "had"); return false } setStatus(a.id, "waiting"); return true });
        next();
        finish();
        return true;
    }
    function next() {
        if (!queue.length) { current = ""; return }
        const app = queue[0];
        queue = queue.slice(1);
        current = app.id;
        setStatus(app.id, "working");
        installer.command = ["sh", "-c", "flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo && " +
                             "flatpak install --user --noninteractive -y flathub \"$1\"", "sh", app.flathub];
        installer.running = true;
    }

    Process {
        id: listInstalled
        command: ["flatpak", "list", "--app", "--columns=application"]
        stdout: StdioCollector { onStreamFinished: win.installedIds = text.split("\n").map(s => s.trim()).filter(s => s) }
    }
    Process {
        id: installer
        property string lastLine: ""
        stderr: SplitParser { onRead: line => { if (line.trim()) installer.lastLine = line.trim().slice(0, 160) } }
        onExited: code => {
            if (code !== 0) { const e = Object.assign({}, win.errors); e[win.current] = installer.lastLine || "flatpak stopped (" + code + ")"; win.errors = e }
            installer.lastLine = "";
            win.setStatus(win.current, code === 0 ? "done" : "failed");
            win.next();
        }
    }
    Process {
        id: marker
        command: ["sh", "-c", "mkdir -p \"$1\" && date -Iseconds > \"$1/first-run-done\"", "sh", win.configDir]
    }
    // First login: open once the desktop has settled, if the marker is missing.
    FileView {
        path: win.configDir + "/first-run-done"
        onLoadFailed: error => { if (error === FileViewError.FileNotFound && Quickshell.env("NEXUS_FIRST_RUN") === "1") firstLogin.start() }
    }
    Timer { id: firstLogin; interval: 2500; onTriggered: win.open() }
    Connections {
        target: win.bridge
        function onResult(id, type, data) {
            if (id !== win.previewId) return;
            for (const i of win.plan.system) win.setStatus(i.id, "approval");
            win.systemNote = "Prepared as one system change, waiting for your approval: " + data.description +
                             ". Nothing is installed for every account until you approve it, and it can be rolled back.";
            win.proposeSystem(data);
        }
        function onFailed(id, message) {
            if (id !== win.previewId) return;
            for (const i of win.plan.system) win.setStatus(i.id, "failed");
            win.systemNote = "The system items could not be prepared: " + message;
        }
    }

    IpcHandler {
        target: "firstrun"
        function open(): void { win.open() }
        function close(): void { win.visible = false }
        // Presses a chip (studying, gaming, office, coding, creative, browsing).
        function choose(id: string): bool { win.open(); return win.toggle(id) }
        // "Or say it in your own words": picks bundles from their keywords.
        function describe(text: string): string { win.open(); return JSON.stringify(win.describe(text)) }
        function remove(key: string): void { win.remove(key) }
        function apply(): bool { return win.apply() }
        function skip(): void { win.skip() }
        function state(): string {
            return JSON.stringify({ visible: win.visible, step: win.step, chosen: win.chosen, matched: win.matchedWords,
                                    apps: win.plan.apps.map(a => a.flathub), system: win.plan.system.map(s => s.package || s.option),
                                    settings: win.plan.settings.map(s => s.key), progress: win.progress, systemNote: win.systemNote })
        }
    }

    // ---- the window ----
    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#0B0F24" }
            GradientStop { position: 0.55; color: "#05071A" }
            GradientStop { position: 1; color: "#020309" }
        }
    }
    Canvas {
        anchors.fill: parent
        onWidthChanged: requestPaint()
        onPaint: {
            const ctx = getContext("2d");
            ctx.reset();
            let seed = 59;
            const rnd = () => { seed = (seed * 1664525 + 1013904223) % 4294967296; return seed / 4294967296 };
            for (let i = 0; i < 70; i++) {
                ctx.fillStyle = "rgba(201,212,255," + (0.25 + rnd() * 0.6).toFixed(2) + ")";
                const s = rnd() < 0.85 ? 1 : 2;
                ctx.fillRect(Math.round(rnd() * width), Math.round(rnd() * height), s, s);
            }
        }
    }
    Text { x: 32; y: 26; text: "Welcome · step " + (win.step === "choose" ? 1 : 2) + " of 2"; color: win.theme.inkSecondary; font.pixelSize: 12 }

    Flickable {
        anchors { fill: parent; topMargin: 18 }
        contentHeight: body.implicitHeight + 40; clip: true
        boundsBehavior: Flickable.StopAtBounds
        Column {
            id: body
            width: Math.min(920, parent.width - 64); x: (parent.width - width) / 2
            spacing: 18
            // The core.
            Shape {
                anchors.horizontalCenter: parent.horizontalCenter
                width: 100; height: 100
                preferredRendererType: Shape.CurveRenderer
                ShapePath {
                    strokeColor: "transparent"
                    fillGradient: RadialGradient {
                        centerX: 50; centerY: 50; centerRadius: 50; focalX: 50; focalY: 50
                        GradientStop { position: 0; color: "#FFFFFF" }
                        GradientStop { position: 0.05; color: "#FFE3B8" }
                        GradientStop { position: 0.16; color: "#FFA65C" }
                        GradientStop { position: 0.34; color: Qt.rgba(0.84, 0.41, 0.25, 0.92) }
                        GradientStop { position: 0.54; color: Qt.rgba(0.55, 0.31, 0.78, 0.6) }
                        GradientStop { position: 0.68; color: Qt.rgba(0.49, 0.42, 1.0, 0.14) }
                        GradientStop { position: 0.73; color: Qt.rgba(0.49, 0.42, 1.0, 0) }
                    }
                    PathAngleArc { centerX: 50; centerY: 50; radiusX: 50; radiusY: 50; sweepAngle: 360 }
                }
            }
            Text {
                width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
                text: win.step === "choose" ? "What will you mostly use this computer for?" : "Setting up " + win.labels.join(" + ")
                color: win.theme.ink; font.pixelSize: 26; font.weight: 600
            }

            // ---- step 1: choose ----
            Flow {
                visible: win.step === "choose"
                anchors.horizontalCenter: parent.horizontalCenter
                width: Math.min(parent.width, chipRow.implicitWidth); spacing: 8
                Row {
                    id: chipRow
                    spacing: 8
                    Repeater {
                        model: win.bundles
                        delegate: Rectangle {
                            required property var modelData
                            readonly property bool on: win.chosen.includes(modelData.id)
                            width: chipText.implicitWidth + 32; height: 38; radius: 19
                            color: on ? "#5F4FE0" : chipHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.2) : "#121B33"
                            border.color: activeFocus ? win.theme.focus : on ? "#5F4FE0" : win.theme.glassBorder
                            border.width: activeFocus ? 2 : 1
                            activeFocusOnTab: true
                            Keys.onSpacePressed: win.toggle(modelData.id)
                            Keys.onReturnPressed: win.toggle(modelData.id)
                            Text { id: chipText; anchors.centerIn: parent; text: modelData.label; color: on ? "#FFFFFF" : win.theme.ink; font.pixelSize: 13 }
                            HoverHandler { id: chipHover; cursorShape: Qt.PointingHandCursor }
                            TapHandler { onTapped: win.toggle(modelData.id) }
                            Accessible.role: Accessible.CheckBox
                            Accessible.checkable: true
                            Accessible.checked: on
                            Accessible.name: modelData.label
                        }
                    }
                }
            }
            Rectangle {
                visible: win.step === "choose"
                anchors.horizontalCenter: parent.horizontalCenter
                width: Math.min(560, parent.width); height: 48; radius: 24
                color: Qt.rgba(0.04, 0.07, 0.15, 0.92); border.color: words.activeFocus ? win.theme.focus : win.theme.glassBorder
                TextInput {
                    id: words
                    x: 20; width: parent.width - 40; anchors.verticalCenter: parent.verticalCenter
                    color: win.theme.ink; font.pixelSize: 14; clip: true
                    Text { visible: !words.text; text: "Or say it in your own words, then press Enter"; color: win.theme.inkSecondary; font.pixelSize: 14 }
                    onAccepted: win.describe(text)
                    Accessible.role: Accessible.EditableText
                    Accessible.name: "Or say it in your own words"
                }
            }
            Text {
                visible: win.step === "choose" && words.text !== "" && win.matchedWords.length === 0 && words.activeFocus === false
                width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
                text: "None of the bundles matched those words. Pick a chip above instead."; color: win.theme.inkSecondary; font.pixelSize: 12
            }

            // What the chosen bundles add up to; anything can be removed.
            Rectangle {
                visible: win.step === "choose" && win.chosen.length > 0
                width: parent.width; height: suggestion.implicitHeight + 44
                radius: 22; color: Qt.rgba(0.04, 0.07, 0.15, 0.94); border.color: Qt.rgba(0.53, 0.66, 1.0, 0.5)
                Column {
                    id: suggestion
                    x: 26; y: 22; width: parent.width - 52; spacing: 16
                    Item {
                        width: parent.width; height: Math.max(heading.implicitHeight, fromWhere.implicitHeight)
                        Column {
                            id: heading
                            spacing: 4
                            Text { text: "NEXUS SUGGESTS"; color: win.theme.inkSecondary; font.pixelSize: 12; font.letterSpacing: 0.5 }
                            Text { text: win.labels.join(" + ") + " setup"; color: win.theme.ink; font.pixelSize: 19; font.weight: 600 }
                        }
                        Text {
                            id: fromWhere
                            anchors { right: parent.right; bottom: parent.bottom }
                            width: Math.min(implicitWidth, parent.width - heading.width - 20); wrapMode: Text.Wrap; horizontalAlignment: Text.AlignRight
                            text: win.matchedWords.length ? "From the curated bundles, matched on: " + win.matchedWords.join(", ") + ". Remove anything you don't want."
                                                          : "From the curated bundles. Remove anything you don't want."
                            color: win.theme.inkSecondary; font.pixelSize: 12; textFormat: Text.PlainText
                        }
                    }
                    Row {
                        width: parent.width; spacing: 20
                        Column {
                            width: (parent.width - 20) / 2; spacing: 6
                            Text { text: "Apps for you · " + win.plan.apps.length; color: win.theme.inkSecondary; font.pixelSize: 12; bottomPadding: 2 }
                            Repeater {
                                model: win.plan.apps
                                delegate: Item_ {
                                    required property var modelData
                                    width: parent.width; item: modelData; dot: "#9C8FFF"
                                    extra: win.installedIds.includes(modelData.flathub) ? " · already installed" : ""
                                }
                            }
                            Text { visible: win.plan.apps.length === 0; text: "None"; color: win.theme.muted; font.pixelSize: 13 }
                        }
                        Column {
                            width: (parent.width - 20) / 2; spacing: 6
                            Text { text: "Settings · " + win.plan.settings.length; color: win.theme.inkSecondary; font.pixelSize: 12; bottomPadding: 2 }
                            Repeater {
                                model: win.plan.settings
                                delegate: Item_ { required property var modelData; width: parent.width; item: modelData; dot: "#FFA65C" }
                            }
                            Text { visible: win.plan.settings.length === 0; text: "No changes"; color: win.theme.muted; font.pixelSize: 13 }
                            Text { visible: win.plan.system.length > 0; topPadding: 8; bottomPadding: 2
                                   text: "For every account · needs approval · " + win.plan.system.length; color: win.theme.inkSecondary; font.pixelSize: 12 }
                            Repeater {
                                model: win.plan.system
                                delegate: Item_ { required property var modelData; width: parent.width; item: modelData; dot: "#7FE0B0" }
                            }
                        }
                    }
                    Rectangle { width: parent.width; height: 1; color: "#1B2647" }
                    Item {
                        width: parent.width; height: Math.max(44, footText.implicitHeight)
                        Column {
                            id: footText
                            anchors.verticalCenter: parent.verticalCenter; width: parent.width - actions.width - 20; spacing: 2
                            Text {
                                width: parent.width; wrapMode: Text.Wrap; color: win.theme.ink; font.pixelSize: 13
                                text: win.plan.system.length ? "Apps install for you only. The " + win.plan.system.length +
                                      (win.plan.system.length === 1 ? " system item is" : " system items are") + " one change that goes through approval."
                                      : "Apps install for you only, with no password."
                            }
                            Text { width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 12
                                   text: "Settings can be undone from Timeline; apps can be removed in Apps." }
                        }
                        Row {
                            id: actions
                            anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                            spacing: 10
                            Button { theme: win.theme; label: "Skip for now"; onClicked: win.skip() }
                            Button { theme: win.theme; primary: true; label: win.plan.system.length ? "Set up and ask for approval" : "Set up"
                                     onClicked: win.apply() }
                        }
                    }
                }
            }
            Row {
                visible: win.step === "choose" && win.chosen.length === 0
                anchors.horizontalCenter: parent.horizontalCenter
                Button { theme: win.theme; label: "Skip for now"; onClicked: win.skip() }
            }

            // ---- step 2: setting up ----
            Rectangle {
                visible: win.step === "setup"
                width: parent.width; height: setupColumn.implicitHeight + 44
                radius: 22; color: Qt.rgba(0.04, 0.07, 0.15, 0.94); border.color: Qt.rgba(0.53, 0.66, 1.0, 0.5)
                Column {
                    id: setupColumn
                    x: 26; y: 22; width: parent.width - 52; spacing: 8
                    Repeater {
                        model: win.plan.apps.concat(win.plan.settings).concat(win.plan.system)
                        delegate: Item {
                            required property var modelData
                            readonly property string status: win.progress[modelData.id] || ""
                            width: setupColumn.width; height: 30
                            Text { id: itemName; anchors.verticalCenter: parent.verticalCenter; text: modelData.name; color: win.theme.ink; font.pixelSize: 14 }
                            Text {
                                visible: status === "failed" && !!win.errors[modelData.id]
                                x: itemName.width + 16; anchors.verticalCenter: parent.verticalCenter; width: parent.width - x - 200
                                elide: Text.ElideRight; textFormat: Text.PlainText
                                text: win.errors[modelData.id] || ""; color: win.theme.muted; font.pixelSize: 12
                            }
                            Text {
                                anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                                font.pixelSize: 13
                                color: status === "failed" ? win.theme.error : status === "done" || status === "had" ? "#7FE0B0" : win.theme.inkSecondary
                                text: ({ waiting: "Waiting", working: "Installing…", done: modelData.flathub ? "Installed" : "Done",
                                         failed: "Did not work", had: "Already installed", approval: "Waiting for your approval" })[status] || ""
                            }
                        }
                    }
                    Text { visible: installer.lastLine !== "" && win.current !== ""; width: parent.width; elide: Text.ElideRight
                           textFormat: Text.PlainText; text: installer.lastLine; color: win.theme.muted; font.pixelSize: 12 }
                    Text { visible: win.systemNote !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; topPadding: 6
                           text: win.systemNote; color: "#C9D4FF"; font.pixelSize: 13 }
                    Rectangle { width: parent.width; height: 1; color: "#1B2647" }
                    Item {
                        width: parent.width; height: Math.max(44, moveInText.implicitHeight)
                        Text {
                            id: moveInText
                            anchors.verticalCenter: parent.verticalCenter; width: parent.width - doneRow.width - 20; wrapMode: Text.Wrap
                            text: "Coming from Windows or a Mac? Nexus can copy your documents and photos from the old drive."
                            color: win.theme.inkSecondary; font.pixelSize: 13
                        }
                        Row {
                            id: doneRow
                            anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                            spacing: 10
                            Button { theme: win.theme; label: "Bring my files over"; onClicked: win.moveIn() }
                            Button { theme: win.theme; primary: true; label: win.busy() ? "Close (keeps going)" : "Done"; onClicked: win.visible = false }
                        }
                    }
                }
            }
        }
    }

    // One removable item: a dot, its name, where it came from, and Remove.
    component Item_: Rectangle {
        id: row
        property var item
        property string dot
        property string extra: ""
        height: 52; radius: 12; color: "#0E1630"; border.color: "#1F2C55"
        Rectangle { x: 14; anchors.verticalCenter: parent.verticalCenter; width: 10; height: 10; radius: 5; color: row.dot }
        Column {
            x: 36; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 36 - 52; spacing: 1
            Text { width: parent.width; elide: Text.ElideRight; text: row.item.name; color: win.theme.ink; font.pixelSize: 14; font.weight: 500 }
            Text { width: parent.width; elide: Text.ElideRight; text: row.item.detail + " · " + row.item.from + row.extra
                   color: win.theme.inkSecondary; font.pixelSize: 12 }
        }
        Rectangle {
            anchors { right: parent.right; rightMargin: 10; verticalCenter: parent.verticalCenter }
            width: 32; height: 32; radius: 9; color: rmHover.hovered ? Qt.rgba(1.0, 0.56, 0.64, 0.18) : "#121B33"
            border.color: activeFocus ? win.theme.focus : win.theme.glassBorder
            activeFocusOnTab: true
            Keys.onReturnPressed: win.remove(row.item.id)
            Keys.onSpacePressed: win.remove(row.item.id)
            Text { anchors.centerIn: parent; text: "✕"; color: win.theme.inkSecondary; font.pixelSize: 13 }
            HoverHandler { id: rmHover; cursorShape: Qt.PointingHandCursor }
            TapHandler { onTapped: win.remove(row.item.id) }
            Accessible.role: Accessible.Button
            Accessible.name: "Remove " + row.item.name
        }
    }
}
