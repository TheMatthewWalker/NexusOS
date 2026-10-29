import QtQuick
import Quickshell

// Settings, Privacy and AI: which model answers, what may leave the
// computer, conversation history, and the notes Nexus reads (the person's own
// "training documents": AssistantContext.cs adds them to every question).
Column {
    id: page
    property var theme
    property var host
    readonly property var bridge: host.bridge
    width: parent ? parent.width : 600
    spacing: 10

    property var providers: []
    property string selected: ""
    property var status: null
    property string message: ""
    property bool confirmClear: false
    property int providersId: -1
    property int statusId: -1
    property int selectId: -1
    property int clearId: -1
    readonly property string notesDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos/assistant"
    readonly property var labels: ({ "local": "On this computer (Ollama)", "llama-cpp": "On this computer (llama.cpp)",
                                     "openai-compatible": "Cloud model" })
    function refresh() {
        if (!bridge.online) return;
        providersId = bridge.send("providers");
        statusId = bridge.send("status");
    }
    Component.onCompleted: refresh()
    Connections {
        target: page.bridge
        function onOnlineChanged() { page.refresh() }
        function onResult(id, type, data) {
            if (id === page.providersId) { page.providers = data.providers || []; page.selected = data.selected || "" }
            else if (id === page.statusId) page.status = data;
            else if (id === page.selectId) { page.selected = data.selected; page.statusId = page.bridge.send("status") }
            else if (id === page.clearId) { page.confirmClear = false; page.message = "Conversation history erased." }
        }
        function onFailed(id, message) {
            if ([page.providersId, page.statusId, page.selectId, page.clearId].includes(id)) page.message = message;
        }
    }

    PageHeader { theme: page.theme; title: "Privacy and AI"
                 summary: "Nexus runs on this computer. Nothing you ask leaves it unless you turn on a cloud model." }

    SettingRow {
        theme: page.theme; label: "Model"; highlighted: page.host.highlight === "model"
        detail: !page.bridge.online ? (page.bridge.offlineReason || "Nexus Core is not running.")
              : !page.status ? "Checking…"
              : page.status.model + " · " + (page.status.available === false ? "not ready (is Ollama running, and the model downloaded?)"
                                                                                : "ready")
    }
    SettingRow {
        theme: page.theme; label: "Where answers come from"; highlighted: page.host.highlight === "provider"
        visible: page.providers.length > 0
        detail: page.selected === "openai-compatible"
            ? "Questions are sent to the cloud provider. Each request is recorded in the activity journal."
            : "Questions stay on this computer."
        Choice { theme: page.theme; name: "Where answers come from"; current: page.selected
                 options: page.providers.map(p => ({ value: p, label: page.labels[p] || p }))
                 onChose: v => page.selectId = page.bridge.send("select", { name: v }) }
    }
    SettingRow {
        theme: page.theme; label: "Your notes for Nexus"; highlighted: page.host.highlight === "notes"
        detail: "Text or Markdown files in " + page.notesDir.replace(Quickshell.env("HOME"), "~") +
                " are read with every question: who you are, how you like answers, what you use this computer for."
        Button { theme: page.theme; label: "Open folder"; onClicked: page.host.openFolder(page.notesDir) }
    }
    SettingRow {
        theme: page.theme; label: "Conversation history"; highlighted: page.host.highlight === "history"
        detail: page.confirmClear ? "Erase every saved conversation? This cannot be undone."
              : "Kept on this computer so follow-up questions make sense, and erased after 30 days."
        Row {
            spacing: 8
            Button { theme: page.theme; visible: !page.confirmClear; danger: true; label: "Erase history"
                     enabled: page.bridge.online; onClicked: page.confirmClear = true }
            Button { theme: page.theme; visible: page.confirmClear; label: "Cancel"; onClicked: page.confirmClear = false }
            Button { theme: page.theme; visible: page.confirmClear; primary: true; danger: true; label: "Erase"
                     onClicked: page.clearId = page.bridge.send("clear-history") }
        }
    }
    SettingRow {
        theme: page.theme; label: "System changes"; highlighted: page.host.highlight === "guardian"
        detail: "Changes to the system itself, such as allowing cloud models for everyone, installing software or " +
                "turning on SSH, go through Guardian: you see the change, approve it, and it can be rolled back."
    }
    Text {
        visible: page.message !== ""
        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13
    }
}
