import QtQuick
import Quickshell
import Quickshell.Io
import "settings"

// Code: the Nexus coding agent (design: "Coding agent A · Conversation and
// diff side by side"). The conversation and the agent's steps on the left;
// the files it changed, as diffs, on the right, with Accept, Reject and Undo.
// Nothing is written to the project until the person accepts. The agent runs
// in Core (CodingAgent.cs) on the selected model; this window only shows it.
// The agent cannot run anything: when it wants the tests run, a card shows the
// exact command, and only the person's Run sends it (once) to Core, which runs
// it in a sandbox. A task from Nexus's chat is filled in, not sent.
// Two views of the same session, chosen at the top and remembered per person
// (~/.config/nexusos/code.json): Split, above, and Timeline (design: "Coding
// agent B · Core timeline"), the agent's steps as one vertical line, with
// each change as a mini-diff and the test request with its Run button. Both
// read the same properties and call the same functions; nothing is copied.
// Opened from the Nexus orbit (the studio role), Super+C, or
// `nexus-shell-ipc call code open [folder]`.
FloatingWindow {
    id: win
    property var theme
    property var bridge
    signal switchToImages()
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
    property var proposal: null        // the agent's request to run the tests: { id, command, files, affects }
    property bool testing: false
    property string testCommand: ""
    property string testOutput: ""
    property bool sandbox: true        // bubblewrap is there; without it Core runs no tests
    property bool canUndo: false
    property int startId: -1
    property int runId: -1
    property int actionId: -1
    property int testId: -1
    property string pendingFolder: ""
    property string pendingTask: ""    // a task from Nexus's chat, filled in once the folder is open; the person sends it

    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    readonly property string configDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos"
    // "split" or "timeline". Without a project the folder chooser shows in either.
    readonly property string view: prefs.view === "timeline" ? "timeline" : "split"
    readonly property bool timeline: view === "timeline" && session !== ""
    function setView(v) {
        if (v !== "split" && v !== "timeline") return;
        prefs.view = v;
        prefsFile.writeAdapter();
    }
    FileView {
        id: prefsFile
        path: win.configDir + "/code.json"
        adapter: JsonAdapter { id: prefs; property string view: "split" }
    }
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
    // From a Nexus action card ("Do it"): open the folder and fill in the task, for the person to read and send.
    function start(dir, task) {
        pendingTask = task || "";
        open(dir);
    }
    function ask(text) {
        if (!session || working || testing || !text.trim()) return;
        error = ""; working = true; testResult = null; proposal = null; testOutput = "";
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
    // Only from the Run button: the one place a test run starts.
    function runTests() {
        if (!proposal || working || testing) return;
        error = ""; testing = true; testCommand = proposal.command; testOutput = "";
        testId = bridge.send("code-run", { session: session, run: proposal.id });
        proposal = null;
    }
    function dismissRun() {
        if (!proposal) return;
        act("code-dismiss", { run: proposal.id });
        proposal = null;
    }

    Connections {
        target: win.bridge
        function onResult(id, type, data) {
            if (id === win.startId) {
                win.session = data.id; win.folder = data.folder; win.tests = data.tests || ""; win.sandbox = data.sandbox !== false;
                win.conversation = []; win.files = []; win.canUndo = false; win.proposal = null; win.testOutput = "";
                // Filled in, not sent: the person reads the task and presses Enter.
                if (win.pendingTask) { prompt.text = win.pendingTask; win.pendingTask = ""; prompt.cursorPosition = prompt.text.length }
                prompt.forceActiveFocus();
            } else if (id === win.testId) {
                win.testing = false;
                win.testResult = { passed: data.passed, summary: data.summary };
                win.testOutput = data.output || "";
                win.updateAgent(a => a.note = (data.passed ? "Tests pass: " : "Tests fail: ") + data.summary +
                                               (data.truncated ? " (output truncated)" : "") + ". Ask Nexus to continue if something needs fixing.");
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
                if (ev.run) win.proposal = ev.run;      // shown as a card; nothing runs until Run
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
            else if (id === win.testId) { win.testing = false; win.error = message }
            else if (id === win.actionId) win.error = message;
        }
    }

    IpcHandler {
        target: "code"
        function open(folder: string): void { win.open(folder) }
        function ask(text: string): void { win.ask(text) }
        // "split" or "timeline", as the toggle at the top.
        function view(name: string): void { win.setView(name) }
        function state(): string {
            return JSON.stringify({ visible: win.visible, view: win.view, session: win.session, folder: win.folder, working: win.working,
                                    files: win.files.map(f => f.path), steps: win.conversation.length ? win.conversation[win.conversation.length - 1].steps.length : 0,
                                    proposal: win.proposal ? win.proposal.command : "", testing: win.testing,
                                    prompt: prompt.text, error: win.error })
        }
    }

    readonly property var dots: ({ read: "#86A8FF", search: "#86A8FF", edit: "#9C8FFF", pass: "#7FE0B0", fail: "#FF8FA3", tests: "#FFA65C" })

    // ---- pieces both views show ----
    component ViewToggle: Choice {
        required property var w
        theme: w.theme; name: "View"; current: w.view
        options: [{ value: "split", label: "Split" }, { value: "timeline", label: "Timeline" }]
        onChose: v => w.setView(v)
    }
    // The agent's request to run the tests: the exact command, run only on the person's Run.
    component RunCard: Rectangle {
        id: rc
        required property var w
        visible: w.proposal !== null || w.testing
        height: runCard.implicitHeight + 24; radius: 12
        color: "#0B1430"; border.color: "#FFA65C"
        Column {
            id: runCard
            x: 12; y: 12; width: parent.width - 24; spacing: 8
            Text { width: parent.width; wrapMode: Text.Wrap; color: rc.w.theme.ink; font.pixelSize: 13; font.weight: 500
                   text: rc.w.testing ? "Running the tests…" : "Nexus asks to run the tests" }
            Text { width: parent.width; wrapMode: Text.WrapAnywhere; textFormat: Text.PlainText
                   text: rc.w.proposal ? rc.w.proposal.command : rc.w.testCommand
                   color: "#C9D4FF"; font.family: "JetBrains Mono"; font.pixelSize: 12; font.features: ({ "calt": 0, "liga": 0 }) }
            Text { width: parent.width; wrapMode: Text.Wrap; color: rc.w.theme.inkSecondary; font.pixelSize: 12
                   text: "On a copy of the project with the staged changes, in a sandbox: no network, and nothing of your home folder but this project. At most 5 minutes." }
            Text { visible: !!rc.w.proposal && rc.w.proposal.affects.length > 0
                   width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; color: "#FFA65C"; font.pixelSize: 12
                   text: rc.w.proposal ? "The changes include " + rc.w.proposal.affects.join(", ") + ", which decide what this command does. Read the diff first." : "" }
            Text { visible: !rc.w.sandbox; width: parent.width; wrapMode: Text.Wrap; color: rc.w.theme.error; font.pixelSize: 12
                   text: "Bubblewrap is not installed, so Nexus cannot run tests safely and will not run them." }
            Row {
                spacing: 8
                Button { visible: !rc.w.testing; theme: rc.w.theme; primary: true; label: "Run"; enabled: !rc.w.working && rc.w.sandbox
                         onClicked: rc.w.runTests() }
                Button { visible: !rc.w.testing; theme: rc.w.theme; label: "Not now"; onClicked: rc.w.dismissRun() }
                Button { visible: rc.w.testing; theme: rc.w.theme; danger: true; label: "Stop"; onClicked: rc.w.act("code-stop") }
            }
        }
    }
    // The end of the last run's output, as the person saw it.
    component TestOutput: Rectangle {
        id: to
        required property var w
        visible: w.testOutput !== "" && !w.testing
        height: Math.min(220, outputText.implicitHeight + 20); radius: 12; clip: true
        color: "#070C1E"; border.color: w.theme.glassBorder
        Text { id: outputText; x: 10; width: parent.width - 20; wrapMode: Text.WrapAnywhere; textFormat: Text.PlainText
               anchors.bottom: parent.bottom; anchors.bottomMargin: 10
               text: to.w.testOutput.slice(-4000); color: "#9AA9C9"; font.family: "JetBrains Mono"; font.pixelSize: 11
               font.features: ({ "calt": 0, "liga": 0 }) }
    }
    // One line of a diff: old and new line numbers, the mark, the code as typed.
    component DiffLine: Rectangle {
        required property var line
        color: line.mark === "+" ? Qt.rgba(0.5, 0.88, 0.69, 0.10) : line.mark === "-" ? Qt.rgba(1, 0.56, 0.64, 0.10) : "transparent"
        Row {
            x: 10; anchors.verticalCenter: parent.verticalCenter; spacing: 0
            Text { width: 40; horizontalAlignment: Text.AlignRight; rightPadding: 10; text: line.a; color: "#7885A3"; font.family: "JetBrains Mono"; font.pixelSize: 12 }
            Text { width: 40; horizontalAlignment: Text.AlignRight; rightPadding: 10; text: line.b; color: "#7885A3"; font.family: "JetBrains Mono"; font.pixelSize: 12 }
            Text { width: 18; text: line.mark === "-" ? "−" : line.mark === "…" ? "" : line.mark; color: "#9AA9C9"; font.family: "JetBrains Mono"; font.pixelSize: 12 }
            Text {
                text: line.mark === "…" ? "⋯" : line.text; textFormat: Text.PlainText
                color: line.mark === "+" ? "#BDF0D6" : line.mark === "-" ? "#FFC7D1" : "#C9D4FF"
                // Code as typed: no ligatures (JetBrains Mono would draw <= as ≤).
                font.family: "JetBrains Mono"; font.pixelSize: 12; font.features: ({ "calt": 0, "liga": 0 })
            }
        }
    }

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
        visible: !win.timeline
        x: 16; y: 16; width: Math.min(460, parent.width * 0.4); height: parent.height - 32
        radius: 18; color: win.theme.glass; border.color: win.theme.glassBorder

        Row {
            id: header
            x: 18; y: 14; width: parent.width - 36; height: 30; spacing: 10
            Rectangle { width: 10; height: 10; radius: 5; anchors.verticalCenter: parent.verticalCenter
                        color: win.working ? "#FFA65C" : "#9C8FFF" }
            Text { text: win.folder ? "Code · " + win.folder : "Code"; color: win.theme.ink; font.pixelSize: 14; font.weight: 500
                   anchors.verticalCenter: parent.verticalCenter; width: parent.width - 80 - splitToggle.width; elide: Text.ElideMiddle }
            ViewToggle { id: splitToggle; w: win; anchors.verticalCenter: parent.verticalCenter }
            Text { text: "Images"; color: win.theme.focus; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter
                   TapHandler { onTapped: win.switchToImages() } }
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
                             (win.tests ? "Nexus can ask to run your tests (" + win.tests + "); they run only when you click Run." : "No test command was found in this project.") }
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
                RunCard { w: win; width: parent.width }
                TestOutput { w: win; width: parent.width }
                Text { visible: win.error !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: win.error; color: win.theme.error; font.pixelSize: 13 }
            }
        }

        Rectangle {
            id: inputBox
            // One message box for both views: it moves to the bottom of the timeline.
            parent: win.timeline ? timelineInput : chatPane
            visible: win.session !== ""
            x: 14; width: parent.width - 28; height: 50; y: parent.height - height - 14
            radius: 14; color: "#0B1430"; border.color: prompt.activeFocus ? win.theme.focus : win.theme.glassBorder
            TextInput {
                id: prompt
                x: 16; width: parent.width - 70; anchors.verticalCenter: parent.verticalCenter
                color: win.theme.ink; font.pixelSize: 14; clip: true; enabled: !win.working && !win.testing
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
        visible: !win.timeline
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
                delegate: DiffLine {
                    required property var modelData
                    line: modelData; width: diffView.width; height: 20
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

    // ---- Timeline: the same session as one line of steps ----
    // Left: Nexus, what it is doing, and the person's requests. Right: every
    // step in order (reads, edits as mini-diffs, the test request with Run,
    // results), then the message box. All from the same properties as Split.
    readonly property int stepCount: conversation.reduce((n, c) => n + (c.role === "agent" ? c.steps.length : 0), 0)
    Item {
        id: aside
        visible: win.timeline
        x: 24; y: 24; width: 290; height: parent.height - 48
        Column {
            width: parent.width; spacing: 14
            // Nexus, warm while it works.
            Item {
                anchors.horizontalCenter: parent.horizontalCenter; width: 150; height: 150
                Rectangle { x: -30; y: 74; width: 210; height: 2; opacity: 0.6
                            gradient: Gradient { orientation: Gradient.Horizontal
                                GradientStop { position: 0; color: "transparent" }
                                GradientStop { position: 0.5; color: "#DCE4FF" }
                                GradientStop { position: 1; color: "transparent" } } }
                Rectangle {
                    anchors.fill: parent; radius: width / 2
                    gradient: Gradient {
                        GradientStop { position: 0; color: win.working || win.testing ? "#FFE3B8" : "#C9D4FF" }
                        GradientStop { position: 0.45; color: win.working || win.testing ? "#D66840" : "#7C6BFF" }
                        GradientStop { position: 1; color: "#05071A" }
                    }
                    opacity: 0.9
                }
            }
            Column {
                width: parent.width; spacing: 3
                Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; color: win.theme.ink; font.pixelSize: 17; font.weight: 500
                       text: win.working ? "Working · step " + (win.stepCount + 1) : win.testing ? "Running the tests"
                           : win.proposal || win.files.length ? "Waiting for you" : "Ready" }
                Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; elide: Text.ElideMiddle
                       color: win.theme.inkSecondary; font.pixelSize: 12; text: win.folder }
            }
            Row {
                anchors.horizontalCenter: parent.horizontalCenter; spacing: 12
                ViewToggle { w: win; anchors.verticalCenter: parent.verticalCenter }
                Text { text: "Images"; color: win.theme.focus; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter
                       TapHandler { onTapped: win.switchToImages() } }
            }
            // What the person asked for, newest last: done, or being worked on.
            Rectangle {
                width: parent.width; height: asked.implicitHeight + 32; radius: 16
                color: win.theme.glass; border.color: win.theme.glassBorder
                Column {
                    id: asked
                    x: 18; y: 16; width: parent.width - 36; spacing: 10
                    Text { text: "YOU ASKED"; color: win.theme.inkSecondary; font.pixelSize: 12; font.letterSpacing: 0.5 }
                    Text { visible: win.conversation.length === 0; width: parent.width; wrapMode: Text.Wrap
                           color: win.theme.inkSecondary; font.pixelSize: 13; text: "Nothing yet. Ask for a change below." }
                    Repeater {
                        model: win.conversation.filter(c => c.role === "user").slice(-6)
                        delegate: Row {
                            required property var modelData
                            required property int index
                            readonly property bool current: win.working && index === Math.min(5, win.conversation.filter(c => c.role === "user").length - 1)
                            spacing: 10
                            Rectangle { width: 14; height: 14; radius: 7; y: 2; border.width: 1.5
                                        border.color: current ? "#86A8FF" : "#7FE0B0"; color: current ? "transparent" : "#7FE0B0" }
                            Text { width: asked.width - 24; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
                                   textFormat: Text.PlainText; text: modelData.text; color: win.theme.ink; font.pixelSize: 13 }
                        }
                    }
                    Row {
                        spacing: 8
                        Button { visible: win.working || win.testing; theme: win.theme; danger: true; label: "Stop"; onClicked: win.act("code-stop") }
                        Button { visible: win.canUndo && win.files.length === 0 && !win.working; theme: win.theme; label: "Undo"
                                 onClicked: win.act("code-undo") }
                    }
                }
            }
        }
    }

    // One step on the line: a status word, what, and the detail.
    component StepRow: Rectangle {
        id: sr
        required property var w
        property string status: "Done"
        property color statusColour: "#7FE0B0"
        property string title: ""
        property string detail: ""
        property bool dashed: false
        height: Math.max(44, stepText.implicitHeight + 24); radius: 14
        color: dashed ? "transparent" : Qt.rgba(0.04, 0.07, 0.15, 0.82); border.color: dashed ? "#2A3A66" : "#1F2C55"
        Text {
            id: stepText
            x: 16; width: parent.width - 32; anchors.verticalCenter: parent.verticalCenter
            wrapMode: Text.Wrap; textFormat: Text.StyledText; font.pixelSize: 13; color: sr.w.theme.inkSecondary
            readonly property var esc: t => String(t).replace(/&/g, "&amp;").replace(/</g, "&lt;")
            text: "<font color='" + sr.statusColour + "'>" + sr.status + "</font>&nbsp;&nbsp;&nbsp;<font color='#E3EAFF'>" + esc(sr.title) +
                  "</font>&nbsp;&nbsp;&nbsp;" + esc(sr.detail)
        }
    }

    Item {
        id: lineArea
        visible: win.timeline
        x: aside.x + aside.width + 36; y: 24; width: parent.width - x - 24; height: parent.height - 48
        Flickable {
            id: lineView
            width: parent.width; height: parent.height - timelineInput.height
            contentHeight: steps.implicitHeight; clip: true
            boundsBehavior: Flickable.StopAtBounds
            onContentHeightChanged: contentY = Math.max(0, contentHeight - height)
            Column {
                id: steps
                width: lineView.width; spacing: 10
                Text { visible: win.conversation.length === 0; width: parent.width; wrapMode: Text.Wrap
                       color: win.theme.inkSecondary; font.pixelSize: 13
                       text: "Each step Nexus takes appears here, in order: what it read, what it would change, and any test run it asks for. Nothing is saved until you accept." }
                Repeater {
                    model: win.conversation
                    delegate: Column {
                        required property var modelData
                        width: steps.width; spacing: 10
                        StepRow { visible: modelData.role === "user"; w: win; width: parent.width
                                  status: "You"; statusColour: "#9C8FFF"; title: modelData.text }
                        Repeater {
                            model: modelData.role === "agent" ? modelData.steps : []
                            delegate: StepRow {
                                required property var modelData
                                w: win; width: steps.width
                                status: modelData.kind === "fail" ? "Failed" : modelData.kind === "tests" ? "Asked" : "Done"
                                statusColour: win.dots[modelData.kind] || "#7FE0B0"
                                title: modelData.title; detail: modelData.detail
                            }
                        }
                        Rectangle {
                            visible: modelData.role === "agent" && modelData.text !== ""
                            width: parent.width; height: answer.implicitHeight + 24; radius: 14
                            color: Qt.rgba(0.04, 0.07, 0.15, 0.82); border.color: "#1F2C55"
                            Text { id: answer; x: 16; y: 12; width: parent.width - 32; wrapMode: Text.Wrap; textFormat: Text.PlainText
                                   text: modelData.text; color: win.theme.ink; font.pixelSize: 13; lineHeight: 1.4 }
                        }
                        StepRow { visible: modelData.role === "agent" && !!modelData.note; w: win; width: parent.width
                                  status: "Result"; title: modelData.note || "" }
                    }
                }
                // Each changed file as a mini-diff, with the same Accept and Reject as Split.
                Repeater {
                    model: win.files
                    delegate: Rectangle {
                        id: card
                        required property var modelData
                        required property int index
                        readonly property var shown: modelData.diff.filter(l => l.mark !== " ").slice(0, 12)
                        width: steps.width; height: cardColumn.implicitHeight; radius: 16; clip: true
                        color: Qt.rgba(0.04, 0.07, 0.15, 0.94); border.color: Qt.rgba(0.53, 0.66, 1, 0.6)
                        Column {
                            id: cardColumn
                            width: parent.width
                            Item {
                                width: parent.width; height: 52
                                Text { x: 16; anchors.verticalCenter: parent.verticalCenter; textFormat: Text.StyledText; font.pixelSize: 13
                                       color: win.theme.ink
                                       text: "<font color='#86A8FF'>Waiting for you</font>&nbsp;&nbsp;&nbsp;" + (card.modelData.created ? "New " : "Edit ") +
                                             card.modelData.path.replace(/&/g, "&amp;").replace(/</g, "&lt;") +
                                             "&nbsp;&nbsp;<font color='#7FE0B0'>+" + card.modelData.added + "</font>" +
                                             (card.modelData.removed ? " <font color='#FFB3C1'>−" + card.modelData.removed + "</font>" : "") }
                                Button { anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
                                         theme: win.theme; label: "Full diff"
                                         onClicked: { win.currentFile = card.index; win.setView("split") } }
                            }
                            Rectangle {
                                width: parent.width; height: miniDiff.implicitHeight + 16; color: "#070B1C"
                                Column {
                                    id: miniDiff
                                    y: 8; width: parent.width
                                    Repeater {
                                        model: card.shown
                                        delegate: DiffLine { required property var modelData; line: modelData; width: miniDiff.width; height: 22 }
                                    }
                                    Text { visible: card.modelData.diff.filter(l => l.mark !== " ").length > card.shown.length
                                           x: 16; height: 22; verticalAlignment: Text.AlignVCenter; color: win.theme.inkSecondary; font.pixelSize: 12
                                           text: "More changes in the full diff" }
                                }
                            }
                            Row {
                                x: 16; height: 60; spacing: 10
                                Button { anchors.verticalCenter: parent.verticalCenter; theme: win.theme; primary: true; enabled: !win.working
                                         label: win.files.length > 1 ? "Accept this file" : "Accept"
                                         onClicked: win.act("code-accept", { files: [card.modelData.path] }) }
                                Button { anchors.verticalCenter: parent.verticalCenter; theme: win.theme; danger: true; enabled: !win.working
                                         label: win.files.length > 1 ? "Reject all" : "Reject"; onClicked: win.act("code-reject") }
                                Button { anchors.verticalCenter: parent.verticalCenter; theme: win.theme; label: "Suggest a change"; enabled: !win.working
                                         onClicked: { prompt.text = "In " + card.modelData.path + ": "; prompt.cursorPosition = prompt.text.length; prompt.forceActiveFocus() } }
                            }
                        }
                    }
                }
                Button { visible: win.files.length > 1; theme: win.theme; primary: true; enabled: !win.working
                         label: "Accept all " + win.files.length + " files"; onClicked: win.act("code-accept", { files: null }) }
                RunCard { w: win; width: parent.width }
                TestOutput { w: win; width: parent.width }
                StepRow { visible: win.testResult !== null && !win.testing; w: win; width: parent.width
                          status: win.testResult && win.testResult.passed ? "Pass" : "Fail"
                          statusColour: win.testResult && win.testResult.passed ? "#7FE0B0" : "#FF8FA3"
                          title: "Tests"; detail: win.testResult ? win.testResult.summary : "" }
                StepRow { visible: win.working; w: win; width: parent.width; dashed: true
                          status: "Next"; statusColour: "#9AA9C9"; title: "Working…" }
                Text { visible: win.error !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: win.error; color: win.theme.error; font.pixelSize: 13 }
            }
        }
        // The message box (inputBox) moves here in this view.
        Item { id: timelineInput; anchors.bottom: parent.bottom; x: -14; width: parent.width + 28; height: 64 }
    }
}
