import QtQuick

// Settings, Automations (feature 9, Automations in plain words). Say what
// should happen and when; Nexus turns the sentence into a rule from a fixed
// list of triggers and actions and shows it back as a sentence before
// anything is saved. Each rule can be paused, deleted, and shows when it
// last ran. Rules only open apps, change personal settings and notify you;
// system changes stay with Guardian.
Column {
    id: page
    property var theme
    property var host
    readonly property var bridge: host.bridge
    width: parent ? parent.width : 600
    spacing: 10

    property var rules: []
    property var draft: null           // { rule, sentence } waiting for Save
    property string draftText: ""
    property string message: ""
    property bool parsing: false
    property int listId: -1
    property int parseId: -1
    property int saveId: -1
    property int changeId: -1
    function refresh() { if (bridge.online) listId = bridge.api("GET", "/rules") }
    Component.onCompleted: refresh()
    Connections {
        target: page.bridge
        function onResult(id, type, data) {
            if (id === page.listId) page.rules = data || [];
            else if (id === page.parseId) {
                page.parsing = false;
                if (data.parsed) { page.draft = data.rule; page.message = "" }
                else { page.draft = null; page.message = data.message || "That did not turn into a rule." }
            } else if (id === page.saveId) { page.draft = null; page.draftText = ""; input.text = ""; page.message = "Saved."; page.refresh() }
            else if (id === page.changeId) page.refresh();
        }
        function onFailed(id, message) {
            if (id === page.parseId) page.parsing = false;
            if ([page.listId, page.parseId, page.saveId, page.changeId].includes(id)) page.message = message;
        }
    }
    function parse() {
        const text = input.text.trim();
        if (!text || parsing) return;
        parsing = true; draft = null; message = ""; draftText = text;
        parseId = bridge.api("POST", "/rules/parse", { sentence: text });
    }
    function save() {
        if (!draft) return;
        saveId = bridge.api("POST", "/rules", { trigger: draft.trigger, conditions: draft.conditions || [], actions: draft.actions,
                                                createdFrom: draftText });
    }

    PageHeader { theme: page.theme; title: "Automations"
                 summary: "Say what should happen, and when. Nexus shows you the rule before anything is saved." }

    Rectangle {
        width: parent.width; height: 48; radius: 12; color: page.theme.chip
        border.color: input.activeFocus ? page.theme.focus : page.theme.glassBorder
        TextInput {
            id: input
            x: 14; width: parent.width - 130; anchors.verticalCenter: parent.verticalCenter
            color: page.theme.ink; font.pixelSize: 14; clip: true
            Text { visible: !input.text; color: page.theme.inkSecondary; font.pixelSize: 14
                   text: "e.g. When I plug in headphones, open Spotify" }
            onAccepted: page.parse()
            Accessible.role: Accessible.EditableText
            Accessible.name: "Describe an automation"
        }
        Button { anchors { right: parent.right; rightMargin: 8; verticalCenter: parent.verticalCenter }
                 theme: page.theme; primary: true; label: page.parsing ? "Reading…" : "Make rule"
                 enabled: page.bridge.online && !page.parsing; onClicked: page.parse() }
    }
    // The draft, as a sentence, with Save.
    Rectangle {
        visible: page.draft !== null
        width: parent.width; height: draftCol.implicitHeight + 28; radius: 12
        color: Qt.rgba(0.49, 0.42, 1.0, 0.12); border.color: page.theme.focus
        Column {
            id: draftCol
            x: 16; y: 14; width: parent.width - 32; spacing: 10
            Text { text: "The rule, as Nexus understood it"; color: page.theme.inkSecondary; font.pixelSize: 12 }
            Text { width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; color: page.theme.ink; font.pixelSize: 15
                   text: page.draft ? page.draft.sentence : "" }
            Row {
                spacing: 8
                Button { theme: page.theme; primary: true; label: "Save"; onClicked: page.save() }
                Button { theme: page.theme; label: "Not quite"; onClicked: { page.draft = null; input.forceActiveFocus() } }
            }
        }
    }
    Text { visible: page.message !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
           text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13 }

    Text { visible: page.rules.length > 0; text: "Your automations"; color: page.theme.inkSecondary; font.pixelSize: 12
           font.weight: 600; topPadding: 6 }
    Repeater {
        model: page.rules
        delegate: SettingRow {
            required property var modelData
            theme: page.theme
            label: modelData.sentence
            detail: (modelData.enabled ? "On" : "Paused") + " · " +
                    (modelData.history && modelData.history.lastRun
                        ? "last ran " + Qt.formatDateTime(new Date(modelData.history.lastRun), "ddd d MMM, HH:mm") + " (" + modelData.history.runs + " times)"
                        : "has not run yet")
            Row {
                spacing: 8
                Switch { theme: page.theme; name: "Automation on"; checked: modelData.enabled; anchors.verticalCenter: parent.verticalCenter
                         onToggled: page.changeId = page.bridge.api("POST", "/rules/" + modelData.id + (modelData.enabled ? "/pause" : "/resume")) }
                Button { theme: page.theme; danger: true; label: "Delete"
                         onClicked: page.changeId = page.bridge.api("DELETE", "/rules/" + modelData.id) }
            }
        }
    }
    SettingRow {
        theme: page.theme; label: "What automations can do"
        detail: "Open apps, change your volume, night light or wallpaper, and show a note. They react to apps opening, " +
                "joining a Wi-Fi network, plugging in the charger, headphones or a screen, and times of day. " +
                "Changes to the system itself are never automatic: they go through Guardian."
    }
}
