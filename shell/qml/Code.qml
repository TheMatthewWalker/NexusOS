import QtQuick
import Quickshell
import Quickshell.Io
import "settings"

// Code: the Nexus coding agent (design: "Coding agent A · Conversation and
// diff side by side"). The conversation and the agent's steps on the left;
// the files it changed, as diffs, on the right, with Accept, Reject and Undo.
// Nothing is written to the project until the person accepts. The agent runs
// in Core (CodingAgent.cs) on the selected model; this window only shows it.
// Opened from the Nexus orbit (the studio role), Super+C, or
// `nexus-shell-ipc call code open [folder]`.
FloatingWindow {
    id: win
    property var theme
    property var bridge
    title: "Code"
    visible: false
    implicitWidth: 1240; implicitHeight: 780
    minimumSize: Qt.size(960, 600)
    color: "#05071A"

    property string session: ""
    property string folder: ""
    property string tests: ""
    property string error: ""
    property bool working: false
    property var conversation: []      // [{ role: "user"|"agent", text, steps: [{ kind, title, detail }] }]
    property var files: []             // [{ path, created, added, removed, diff: [{ a, b, mark, text }] }]
    property int currentFile: 0
    property var testResult: null      // { passed, summary }
    property bool canUndo: false
    property int startId: -1
    property int runId: -1
    property int actionId: -1
    property string pendingFolder: ""

    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    function open(dir) {
        if (dir) openFolder(dir);
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Code)$"]);
        visible = true;
        (session ? prompt : folderField).forceActiveFocus();
    }
    function openFolder(dir) {
        error = "";
        if (!bridge.online) { error = "Nexus Core is not running: " + bridge.offlineReason; return }
        pendingFolder = dir;
        startId = bridge.send("code-start", { folder: dir });
    }
    function ask(text) {
        if (!session || working || !text.trim()) return;
        error = ""; working = true; testResult = null;
        conversation = conversation.concat([{ role: "user", text: text.trim(), steps: [] }, { role: "agent", text: "", steps: [] }]);
        runId = bridge.send("code-send", { session: session, text: text.trim() });
    }
    function updateAgent(change) {
        const c = conversation.slice();
        const last = Object.assign({}, c[c.length - 1]);
        change(last);
        c[c.length - 1] = last;
        conversation = c;
    }
    function act(op, extra) { actionId = bridge.send(op, Object.assign({ session: session }, extra || {})) }

    Connections {
        target: win.bridge
        function onResult(id, type, data) {
            if (id === win.startId) {
                win.session = data.id; win.folder = data.folder; win.tests = data.tests || "";
                win.conversation = []; win.files = []; win.canUndo = false;
                prompt.forceActiveFocus();
            } else if (id === win.actionId) {
                if (data.type === "accepted") { win.files = []; win.canUndo = true;
                    win.updateAgent(a => a.note = "Saved " + data.files.length + " file" + (data.files.length === 1 ? "" : "s") + ".") }
                else if (data.type === "rejected") { win.files = []; win.updateAgent(a => a.note = "Changes rejected; your files are as they were.") }
                else if (data.type === "undone") { win.canUndo = false; win.updateAgent(a => a.note = "Undone: the files are back as they were.") }
            }
        }
        function onCodeEvent(id, ev) {
            if (id !== win.runId) return;
            if (ev.type === "step") {
                win.updateAgent(a => a.steps = a.steps.concat([{ kind: ev.kind, title: ev.title, detail: ev.detail }]));
                if (ev.tests) win.testResult = ev.tests;
            } else if (ev.type === "changes") {
                win.files = ev.files; win.currentFile = 0;
            } else if (ev.type === "message") {
                win.updateAgent(a => a.text = ev.text);
            } else if (ev.type === "done") {
                win.working = false;
            }
        }
        function onFailed(id, message) {
            if (id === win.startId) win.error = message;
            else if (id === win.runId) { win.working = false; win.error = message }
            else if (id === win.actionId) win.error = message;
        }
    }

    IpcHandler {
        target: "code"
        function open(folder: string): void { win.open(folder) }
        function ask(text: string): void { win.ask(text) }
        function state(): string {
            return JSON.stringify({ visible: win.visible, session: win.session, folder: win.folder, working: win.working,
                                    files: win.files.map(f => f.path), steps: win.conversation.length ? win.conversation[win.conversation.length - 1].steps.length : 0,
                                    error: win.error })
        }
    }

    readonly property var dots: ({ read: "#86A8FF", search: "#86A8FF", edit: "#9C8FFF", pass: "#7FE0B0", fail: "#FF8FA3", tests: "#FFA65C" })

    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#0B0F24" }
            GradientStop { position: 1; color: "#020309" }
        }
    }

    // ---- left: the conversation ----
    Rectangle {
        id: chatPane
        x: 16; y: 16; width: Math.min(460, parent.width * 0.4); height: parent.height - 32
        radius: 18; color: win.theme.glass; border.color: win.theme.glassBorder

        Row {
            id: header
            x: 18; y: 14; width: parent.width - 36; height: 30; spacing: 10
            Rectangle { width: 10; height: 10; radius: 5; anchors.verticalCenter: parent.verticalCenter
                        color: win.working ? "#FFA65C" : "#9C8FFF" }
            Text { text: win.folder ? "Code · " + win.folder : "Code"; color: win.theme.ink; font.pixelSize: 14; font.weight: 500
                   anchors.verticalCenter: parent.verticalCenter; width: parent.width - 120; elide: Text.ElideMiddle }
            Text { text: "Local model"; color: "#7FE0B0"; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter }
        }

        // No project yet: choose a folder.
        Column {
            visible: win.session === ""
            x: 18; y: 70; width: parent.width - 36; spacing: 12
            Text { width: parent.width; wrapMode: Text.Wrap; color: win.theme.ink; font.pixelSize: 15
                   text: "Which project should Nexus work on?" }
            Text { width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 13
                   text: "A folder in your home. Nexus reads and edits files only inside it, shows you every change, and saves nothing until you accept." }
            Rectangle {
                width: parent.width; height: 42; radius: 12; color: "transparent"
                border.color: folderField.activeFocus ? win.theme.focus : win.theme.glassBorder
                TextInput {
                    id: folderField
                    x: 12; width: parent.width - 24; anchors.verticalCenter: parent.verticalCenter
                    color: win.theme.ink; font.pixelSize: 14; clip: true; text: "~/"
                    onAccepted: win.openFolder(text.trim())
                    Accessible.role: Accessible.EditableText
                    Accessible.name: "Project folder"
                }
            }
            Button { theme: win.theme; primary: true; label: "Open project"; onClicked: win.openFolder(folderField.text.trim()) }
        }

        Flickable {
            id: talk
            visible: win.session !== ""
            x: 18; y: 56; width: parent.width - 36; height: parent.height - 56 - inputBox.height - 28
            contentHeight: talkColumn.implicitHeight; clip: true
            boundsBehavior: Flickable.StopAtBounds
            onContentHeightChanged: contentY = Math.max(0, contentHeight - height)
            Column {
                id: talkColumn
                width: talk.width; spacing: 12
                Text { visible: win.conversation.length === 0; width: parent.width; wrapMode: Text.Wrap
                       color: win.theme.inkSecondary; font.pixelSize: 13
                       text: "Ask for a change: fix a bug, add a test, explain a file. " +
                             (win.tests ? "Nexus can run your tests (" + win.tests + ")." : "No test command was found in this project.") }
                Repeater {
                    model: win.conversation
                    delegate: Column {
                        required property var modelData
                        width: talkColumn.width; spacing: 8
                        // The person's message, in a bubble.
                        Rectangle {
                            visible: modelData.role === "user"
                            width: Math.min(parent.width, userText.implicitWidth + 28); height: userText.implicitHeight + 18
                            anchors.right: parent.right; radius: 12; color: Qt.rgba(0.37, 0.31, 0.88, 0.35)
                            Text { id: userText; x: 14; y: 9; width: Math.min(talkColumn.width - 28, implicitWidth)
                                   wrapMode: Text.Wrap; textFormat: Text.PlainText; text: modelData.text; color: win.theme.ink; font.pixelSize: 13 }
                        }
                        // The agent's steps and answer.
                        Repeater {
                            model: modelData.role === "agent" ? modelData.steps : []
                            delegate: Row {
                                required property var modelData
                                spacing: 10
                                Rectangle { width: 8; height: 8; radius: 4; y: 5; color: win.dots[modelData.kind] || "#86A8FF" }
                                Text { width: talkColumn.width - 18; wrapMode: Text.Wrap; textFormat: Text.StyledText; font.pixelSize: 13
                                       color: win.theme.inkSecondary
                                       text: "<font color='#E3EAFF'>" + modelData.title + "</font> " + modelData.detail.replace(/&/g, "&amp;").replace(/</g, "&lt;") }
                            }
                        }
                        Text {
                            visible: modelData.role === "agent" && modelData.text !== ""
                            width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                            text: modelData.text; color: win.theme.ink; font.pixelSize: 13; lineHeight: 1.4
                        }
                        Text {
                            visible: modelData.role === "agent" && !!modelData.note
                            width: parent.width; wrapMode: Text.Wrap; text: modelData.note || ""
                            color: "#7FE0B0"; font.pixelSize: 12
                        }
                    }
                }
                Text { visible: win.working; text: "Working…"; color: "#FFA65C"; font.pixelSize: 13 }
                Text { visible: win.error !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: win.error; color: win.theme.error; font.pixelSize: 13 }
            }
        }

        Rectangle {
            id: inputBox
            visible: win.session !== ""
            x: 14; width: parent.width - 28; height: 50; y: parent.height - height - 14
            radius: 14; color: "#0B1430"; border.color: prompt.activeFocus ? win.theme.focus : win.theme.glassBorder
            TextInput {
                id: prompt
                x: 16; width: parent.width - 70; anchors.verticalCenter: parent.verticalCenter
                color: win.theme.ink; font.pixelSize: 14; clip: true; enabled: !win.working
                Text { visible: !prompt.text; text: win.working ? "Nexus is working…" : "Ask for a change, or reply"
                       color: win.theme.inkSecondary; font.pixelSize: 14 }
                onAccepted: { win.ask(text); text = "" }
                Accessible.role: Accessible.EditableText
                Accessible.name: "Message the coding agent"
            }
            Rectangle {
                width: 36; height: 36; radius: 18; anchors { right: parent.right; rightMargin: 8; verticalCenter: parent.verticalCenter }
                color: win.working ? win.theme.glassBorder : win.theme.primary
                Image { anchors.centerIn: parent; width: 18; height: 18; source: "icons/send.svg"; sourceSize: Qt.size(36, 36) }
                TapHandler { enabled: !win.working; onTapped: { win.ask(prompt.text); prompt.text = "" } }
                Accessible.role: Accessible.Button
                Accessible.name: "Send"
            }
        }
    }

    // ---- right: the changes ----
    Item {
        x: chatPane.x + chatPane.width + 16; y: 16; width: parent.width - x - 16; height: parent.height - 32

        Text {
            visible: win.files.length === 0
            anchors.centerIn: parent; width: parent.width * 0.7; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
            color: win.theme.inkSecondary; font.pixelSize: 14
            text: !win.session ? "Changes appear here, line by line, before anything is saved."
                : win.canUndo ? "Saved. Undo puts the files back as they were." : "No changes yet."
        }
        Button {
            visible: win.files.length === 0 && win.canUndo
            anchors { horizontalCenter: parent.horizontalCenter; top: parent.verticalCenter; topMargin: 36 }
            theme: win.theme; label: "Undo"; onClicked: win.act("code-undo")
        }

        // File tabs.
        Row {
            id: tabs
            visible: win.files.length > 0
            width: parent.width; height: 40; spacing: 6
            Repeater {
                model: win.files
                delegate: Rectangle {
                    required property var modelData
                    required property int index
                    readonly property bool on: index === win.currentFile
                    width: tabText.implicitWidth + 24; height: 34; radius: 10
                    color: on ? win.theme.glass : "transparent"; border.color: on ? win.theme.focus : win.theme.glassBorder
                    Text {
                        id: tabText; anchors.centerIn: parent; textFormat: Text.StyledText; font.pixelSize: 13; color: win.theme.ink
                        text: modelData.path.split("/").pop() + "  <font color='#7FE0B0'>+" + modelData.added + "</font>" +
                              (modelData.removed ? " <font color='#FFB3C1'>−" + modelData.removed + "</font>" : "")
                    }
                    TapHandler { onTapped: win.currentFile = index }
                    Accessible.role: Accessible.PageTab
                    Accessible.name: modelData.path
                }
            }
        }

        // The diff.
        Rectangle {
            visible: win.files.length > 0
            y: 46; width: parent.width; height: parent.height - 46 - footer.height - 10
            radius: 16; color: "#070C1E"; border.color: win.theme.glassBorder
            Text {
                x: 16; y: 10; color: win.theme.inkSecondary; font.pixelSize: 12
                text: win.files.length ? (win.files[win.currentFile].created ? "New file · " : "") + win.files[win.currentFile].path : ""
            }
            ListView {
                id: diffView
                x: 0; y: 34; width: parent.width; height: parent.height - 40
                clip: true
                model: win.files.length ? win.files[win.currentFile].diff : []
                delegate: Rectangle {
                    required property var modelData
                    width: diffView.width; height: 20
                    color: modelData.mark === "+" ? Qt.rgba(0.5, 0.88, 0.69, 0.10) : modelData.mark === "-" ? Qt.rgba(1, 0.56, 0.64, 0.10) : "transparent"
                    Row {
                        x: 10; anchors.verticalCenter: parent.verticalCenter; spacing: 0
                        Text { width: 40; horizontalAlignment: Text.AlignRight; rightPadding: 10; text: modelData.a; color: "#7885A3"; font.family: "JetBrains Mono"; font.pixelSize: 12 }
                        Text { width: 40; horizontalAlignment: Text.AlignRight; rightPadding: 10; text: modelData.b; color: "#7885A3"; font.family: "JetBrains Mono"; font.pixelSize: 12 }
                        Text { width: 18; text: modelData.mark === "-" ? "−" : modelData.mark === "…" ? "" : modelData.mark; color: "#9AA9C9"; font.family: "JetBrains Mono"; font.pixelSize: 12 }
                        Text {
                            text: modelData.mark === "…" ? "⋯" : modelData.text; textFormat: Text.PlainText
                            color: modelData.mark === "+" ? "#BDF0D6" : modelData.mark === "-" ? "#FFC7D1" : "#C9D4FF"
                            // Code as typed: no ligatures (JetBrains Mono would draw <= as ≤).
                            font.family: "JetBrains Mono"; font.pixelSize: 12; font.features: ({ "calt": 0, "liga": 0 })
                        }
                    }
                }
            }
        }

        // Tests, and accept or reject.
        Item {
            id: footer
            visible: win.files.length > 0
            anchors.bottom: parent.bottom; width: parent.width; height: 40
            Row {
            spacing: 10; anchors.verticalCenter: parent.verticalCenter
            Rectangle {
                visible: win.testResult !== null
                height: 30; width: testText.implicitWidth + 24; radius: 15; anchors.verticalCenter: parent.verticalCenter
                color: win.testResult && win.testResult.passed ? Qt.rgba(0.5, 0.88, 0.69, 0.15) : Qt.rgba(1, 0.56, 0.64, 0.15)
                Text { id: testText; anchors.centerIn: parent; font.pixelSize: 12
                       color: win.testResult && win.testResult.passed ? "#7FE0B0" : "#FF8FA3"
                       text: win.testResult ? (win.testResult.passed ? "Tests pass · " : "Tests fail · ") + win.testResult.summary : "" }
            }
            Rectangle {
                height: 30; width: noSys.implicitWidth + 24; radius: 15; anchors.verticalCenter: parent.verticalCenter
                color: "transparent"; border.color: win.theme.glassBorder
                Text { id: noSys; anchors.centerIn: parent; text: "No system changes"; color: win.theme.inkSecondary; font.pixelSize: 12 }
            }
            }
            Row {
                id: actions
                spacing: 8; anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                Button { theme: win.theme; danger: true; label: "Reject"; enabled: !win.working; onClicked: win.act("code-reject") }
                Button { theme: win.theme; label: "Accept this file"; visible: win.files.length > 1; enabled: !win.working
                         onClicked: win.act("code-accept", { files: [win.files[win.currentFile].path] }) }
                Button { theme: win.theme; primary: true; enabled: !win.working
                         label: "Accept " + win.files.length + " file" + (win.files.length === 1 ? "" : "s")
                         onClicked: win.act("code-accept", { files: null }) }
            }
        }
    }
}
