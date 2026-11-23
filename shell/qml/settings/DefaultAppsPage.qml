import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/storage.mjs" as S

// Settings, Default apps: which app opens web links, email, folders, music,
// videos, pictures, text files and PDFs. nexus-default-apps (as you) finds the
// apps that can and writes ~/.config/mimeapps.list, which every app reads.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    property var categories: []
    property string message: ""
    property string open: ""

    Component.onCompleted: lister.running = true
    Process { id: lister; command: ["nexus-default-apps", "list"]
              stdout: StdioCollector { onStreamFinished: page.categories = S.parse(text).categories || [] } }
    Process {
        id: setter
        stdout: StdioCollector { id: setterOut }
        onExited: { const r = S.parse(setterOut.text); page.message = r.error || r.message || ""; page.open = ""; lister.running = true }
    }
    function choose(category, app) {
        if (setter.running || !/^[A-Za-z0-9][A-Za-z0-9._-]*\.desktop$/.test(app)) return;
        setter.command = ["nexus-default-apps", "set", category, app];
        setter.running = true;
    }
    function nameOf(c, id) { const a = (c.apps || []).find(a => a.id === id); return a ? a.name : "None" }

    PageHeader { theme: page.theme; title: "Default apps"; summary: "Which app opens links, email, folders, music, videos, pictures and documents." }
    Text {
        visible: page.message !== ""
        width: page.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13
    }
    Repeater {
        model: page.categories
        delegate: Column {
            required property var modelData
            width: page.width; spacing: 6
            SettingRow {
                theme: page.theme; label: modelData.label; highlighted: page.host.highlight === "default-" + modelData.id
                detail: modelData.apps.length === 0 ? "No app installed can open these. Find one in Apps."
                      : page.nameOf(modelData, modelData.current) + (modelData.apps.length > 1 ? "" : " (the only one installed)")
                Button { visible: modelData.apps.length > 1; theme: page.theme; label: page.open === modelData.id ? "Close" : "Change"
                         onClicked: page.open = page.open === modelData.id ? "" : modelData.id }
            }
            Flow {
                visible: page.open === modelData.id
                width: page.width; spacing: 6
                Repeater {
                    model: page.open === modelData.id ? modelData.apps : []
                    delegate: Button {
                        required property var modelData
                        theme: page.theme; label: modelData.name
                        onClicked: page.choose(page.open, modelData.id)
                    }
                }
            }
        }
    }
}
