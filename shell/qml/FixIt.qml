import QtQuick
import Quickshell
import Quickshell.Io
import "settings"

// Fix it (feature 2): say what is wrong ("my sound stopped working"); Core
// runs the read-only checks for that area (FixIt.cs), says what happened in
// one sentence, shows what it checked, and offers fixes from its library. A
// personal fix runs on Apply; a system fix is a Guardian proposal. Nothing
// runs until the person chooses. Opened from Settings, Health, Nexus, or
// `nexus-shell-ipc call fixit open "<problem>"`.
NexusWindow {
    id: win
    property var theme
    property var bridge
    title: "Fix it"
    visible: false
    implicitWidth: 760; implicitHeight: 640
    minimumSize: Qt.size(620, 520)
    color: "#05071A"

    property string problem: ""
    property var diagnosis: null
    property bool checking: false
    property string message: ""
    property string applied: ""
    property int diagnoseId: -1
    property int applyId: -1
    property string applyingFix: ""
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"

    function open(text) {
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Fix it)$"]);
        visible = true;
        if (text) diagnose(text); else { diagnosis = null; problemField.forceActiveFocus() }
    }
    function diagnose(text) {
        if (!text.trim()) return;
        if (!bridge.online) { message = "Nexus Core is not running: " + bridge.offlineReason; return }
        problem = text.trim(); diagnosis = null; message = ""; applied = ""; checking = true;
        diagnoseId = bridge.api("POST", "/fixit", { problem: problem });
    }
    function apply(fix) {
        applyingFix = fix.id; message = "";
        applyId = bridge.api("POST", "/fixit/apply", { fixId: fix.id, evidenceToken: diagnosis.evidenceToken });
    }
    Connections {
        target: win.bridge
        function onResult(id, type, data) {
            if (id === win.diagnoseId) { win.checking = false; win.diagnosis = data }
            else if (id === win.applyId) {
                win.applied = win.applyingFix;
                win.message = data.applied ? "Done. If it is not fixed, describe what you see now and check again."
                    : data.status === "needs-approval" || data.proposal ? "This needs your approval as a system change: it has been prepared for Guardian."
                    : "That fix could not be applied (" + (data.status || "unknown") + ").";
            }
        }
        function onFailed(id, message) {
            if (id === win.diagnoseId) { win.checking = false; win.message = message }
            else if (id === win.applyId) win.message = message;
        }
    }
    IpcHandler {
        target: "fixit"
        function open(problem: string): void { win.open(problem) }
        function state(): string { return JSON.stringify({ visible: win.visible, checking: win.checking, diagnosis: win.diagnosis, message: win.message }) }
    }

    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#0B0F24" }
            GradientStop { position: 1; color: "#020309" }
        }
    }
    Flickable {
        anchors { fill: parent; margins: 28 }
        contentHeight: body.implicitHeight; clip: true
        boundsBehavior: Flickable.StopAtBounds
        Column {
            id: body
            width: parent.width; spacing: 14
            Text { text: "Fix it"; color: win.theme.ink; font.pixelSize: 24; font.weight: 600 }
            Rectangle {
                width: parent.width; height: 50; radius: 14; color: win.theme.glass
                border.color: problemField.activeFocus ? win.theme.focus : win.theme.glassBorder
                TextInput {
                    id: problemField
                    x: 16; width: parent.width - 130; anchors.verticalCenter: parent.verticalCenter
                    color: win.theme.ink; font.pixelSize: 15; clip: true
                    Text { visible: !problemField.text; color: win.theme.inkSecondary; font.pixelSize: 15
                           text: "What is wrong? e.g. my sound stopped working" }
                    onAccepted: { win.diagnose(text); text = "" }
                    Accessible.role: Accessible.EditableText
                    Accessible.name: "Describe the problem"
                }
                Button { anchors { right: parent.right; rightMargin: 8; verticalCenter: parent.verticalCenter }
                         theme: win.theme; primary: true; label: win.checking ? "Checking…" : "Check"; enabled: !win.checking
                         onClicked: { win.diagnose(problemField.text); problemField.text = "" } }
            }
            Text { visible: win.problem !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                   text: "You said: “" + win.problem + "”"; color: win.theme.inkSecondary; font.pixelSize: 13 }

            // What happened.
            Rectangle {
                visible: !!win.diagnosis
                width: parent.width; height: what.implicitHeight + 28; radius: 14; color: win.theme.glass; border.color: win.theme.glassBorder
                Column {
                    id: what
                    x: 18; y: 14; width: parent.width - 36; spacing: 6
                    Text { text: "WHAT HAPPENED"; color: win.theme.inkSecondary; font.pixelSize: 11; font.letterSpacing: 1 }
                    Text {
                        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; color: win.theme.ink; font.pixelSize: 16; font.weight: 600
                        text: !win.diagnosis ? "" : win.diagnosis.cause || win.diagnosis.message || "Nexus did not find a known problem in what it checked."
                    }
                }
            }
            // What it checked.
            Column {
                visible: !!win.diagnosis && (win.diagnosis.probes || []).length > 0
                width: parent.width; spacing: 6
                Text { text: "What Nexus checked (read-only)"; color: win.theme.ink; font.pixelSize: 14; font.weight: 600 }
                Repeater {
                    model: win.diagnosis ? win.diagnosis.probes : []
                    delegate: Text {
                        required property var modelData
                        textFormat: Text.PlainText; font.pixelSize: 13
                        color: modelData.status === "ok" ? "#7FE0B0" : modelData.status === "failed" ? win.theme.error : win.theme.inkSecondary
                        text: (modelData.status === "ok" ? "✓  " : modelData.status === "failed" ? "!  " : "–  ") + modelData.id +
                              (modelData.status === "ok" ? "" : " (" + modelData.status + ")")
                    }
                }
                Repeater {
                    model: win.diagnosis ? win.diagnosis.evidence : []
                    delegate: Rectangle {
                        required property var modelData
                        width: body.width; height: ev.implicitHeight + 16; radius: 8; color: "#070C1E"
                        Text { id: ev; x: 10; y: 8; width: parent.width - 20; wrapMode: Text.Wrap; textFormat: Text.PlainText
                               text: modelData.probe + ": " + modelData.excerpt; color: win.theme.inkSecondary
                               font.family: "JetBrains Mono"; font.pixelSize: 11 }
                    }
                }
            }
            // Fixes.
            Column {
                visible: !!win.diagnosis && (win.diagnosis.fixes || []).length > 0
                width: parent.width; spacing: 8
                Text { text: "FIXES"; color: win.theme.inkSecondary; font.pixelSize: 11; font.letterSpacing: 1 }
                Repeater {
                    model: win.diagnosis ? win.diagnosis.fixes : []
                    delegate: SettingRow {
                        required property var modelData
                        theme: win.theme
                        label: modelData.label
                        detail: modelData.kind === "system" ? "A system change, so it goes through approval" : "A personal setting; Undo is in Timeline"
                        Button { theme: win.theme; primary: modelData.kind !== "system"
                                 label: win.applied === modelData.id ? "Done" : modelData.kind === "system" ? "Review" : "Apply"
                                 enabled: win.applied !== modelData.id; onClicked: win.apply(modelData) }
                    }
                }
            }
            Text { visible: win.message !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                   text: win.message; color: win.theme.inkSecondary; font.pixelSize: 13 }
            Text {
                width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 12; topPadding: 8
                text: "Fix it only reads allowed diagnostics and picks from known fixes. Nothing runs until you choose."
            }
        }
    }
}
