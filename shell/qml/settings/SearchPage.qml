import QtQuick
import Quickshell

// Settings, Search: which folders private search may read (feature 8). Only
// the folders chosen here are indexed; Core refuses hidden folders, browser
// profiles and key stores, and the index stays in Core's own 0700 state
// folder. Removing a folder removes its files from the index; Erase removes
// the whole index and forgets every folder.
Column {
    id: page
    property var theme
    property var host
    readonly property var bridge: host.bridge
    width: parent ? parent.width : 600
    spacing: 10

    property var status: null
    property string message: ""
    property bool confirmErase: false
    property int statusId: -1
    property int changeId: -1
    readonly property string home: Quickshell.env("HOME") || ""
    readonly property var common: ["Documents", "Desktop", "Pictures", "Downloads", "Music", "Videos"]
    readonly property var sources: status ? status.sources : []

    function refresh() { if (bridge.online) statusId = bridge.api("GET", "/search/status") }
    function add(path) {
        let p = path.trim();
        if (!p) return;
        if (p.startsWith("~")) p = home + p.slice(1);
        if (!p.startsWith("/")) p = home + "/" + p;
        message = ""; changeId = bridge.api("POST", "/search/sources", { paths: [p] });
    }
    function remove(path) { message = ""; changeId = bridge.api("DELETE", "/search/sources?path=" + encodeURIComponent(path)) }
    Component.onCompleted: refresh()
    Timer { interval: 3000; repeat: true; running: !!page.status && page.status.running; onTriggered: page.refresh() }
    Connections {
        target: page.bridge
        function onOnlineChanged() { page.refresh() }
        function onResult(id, type, data) {
            if (id === page.statusId) page.status = data;
            else if (id === page.changeId) {
                page.confirmErase = false;
                if (data.erased) page.message = "The search index is erased, and no folders are searched.";
                else if (data.removedFiles !== undefined) page.message = data.removedFiles + " files were taken out of the index.";
                else if (data.queued) page.message = "Reading the folders again.";
                else if (data.indexing) page.message = "Added. Reading the new folder now.";
                page.refresh();
            }
        }
        function onFailed(id, message) { if (id === page.changeId || id === page.statusId) page.message = message }
    }

    PageHeader { theme: page.theme; title: "Search"
                 summary: "Choose which folders Nexus may search. Nothing outside them is read, and the index never leaves this computer." }
    SettingRow {
        theme: page.theme; visible: !page.bridge.online; label: "Nexus Core is not running"
        detail: "Search needs Nexus Core. " + page.bridge.offlineReason
    }

    Text { text: "Searched folders"; color: page.theme.ink; font.pixelSize: 14; font.weight: 600; topPadding: 4 }
    SettingRow {
        theme: page.theme; visible: !!page.status && page.sources.length === 0
        label: "None yet"; detail: "Add a folder below. Search finds text in documents, notes, spreadsheets, code and saved mail."
    }
    Repeater {
        model: page.sources
        delegate: SettingRow {
            required property var modelData
            theme: page.theme
            label: modelData.path.startsWith(page.home + "/") ? modelData.path.slice(page.home.length + 1) : modelData.path
            detail: modelData.watching ? "Kept up to date as files change" : "Checked every few minutes"
            Button { theme: page.theme; label: "Remove"; onClicked: page.remove(modelData.path) }
        }
    }

    Text { text: "Add a folder"; color: page.theme.ink; font.pixelSize: 14; font.weight: 600; topPadding: 8 }
    Flow {
        width: parent.width; spacing: 8
        Repeater {
            model: page.common.filter(name => !page.sources.some(s => s.path === page.home + "/" + name))
            delegate: Button { required property string modelData; theme: page.theme; label: "+ " + modelData
                               enabled: page.bridge.online; onClicked: page.add(modelData) }
        }
    }
    Rectangle {
        width: parent.width; height: 44; radius: 12; color: page.theme.chip
        border.color: pathField.activeFocus ? page.theme.focus : page.theme.glassBorder
        TextInput {
            id: pathField
            x: 14; width: parent.width - addButton.width - 40; anchors.verticalCenter: parent.verticalCenter
            color: page.theme.ink; font.pixelSize: 13; clip: true
            Text { visible: !pathField.text; text: "Another folder, e.g. ~/Work/Reports"; color: page.theme.inkSecondary; font.pixelSize: 13 }
            onAccepted: { page.add(text); text = "" }
            Accessible.role: Accessible.EditableText
            Accessible.name: "Folder to search"
        }
        Button { id: addButton; anchors { right: parent.right; rightMargin: 6; verticalCenter: parent.verticalCenter }
                 theme: page.theme; label: "Add"; enabled: page.bridge.online && pathField.text.trim() !== ""
                 onClicked: { page.add(pathField.text); pathField.text = "" } }
    }

    Text { text: "The index"; color: page.theme.ink; font.pixelSize: 14; font.weight: 600; topPadding: 8 }
    SettingRow {
        theme: page.theme; label: "What is indexed"
        detail: !page.status ? "…" : page.status.files + " files, " + page.status.chunks + " passages" +
                (page.status.running ? ". Reading now…" : page.status.lastRun ? ". Last checked " +
                 new Date(page.status.lastRun.finishedAt).toLocaleString(Qt.locale(), "d MMM HH:mm") + "." : ".")
        Button { theme: page.theme; label: "Read again"; enabled: page.bridge.online && page.sources.length > 0
                 onClicked: { page.message = ""; page.changeId = page.bridge.api("POST", "/search/reindex") } }
    }
    SettingRow {
        theme: page.theme; label: "Search by meaning"
        detail: !page.status ? "…" : !page.status.embeddings.model ? "Off: results match your words only."
              : page.status.embeddings.available === false ? "Not available: " + (page.status.embeddings.reason || "the embedding model is missing").replace(/\.+$/, "") + ". Results match your words only."
              : page.status.embeddedChunks + " of " + page.status.chunks + " passages can be found by meaning, with the small local model " + page.status.embeddings.model + "."
    }
    SettingRow {
        theme: page.theme; visible: !!page.status && page.status.errors.length > 0
        label: "Files that could not be read"
        detail: page.status ? page.status.errors.slice(0, 4).map(e => e.path.split("/").pop() + ": " + e.reason).join("\n") : ""
    }
    SettingRow {
        theme: page.theme; label: "Erase the index"
        detail: page.confirmErase ? "This deletes everything the index holds and stops searching every folder. Your files are not touched."
                                  : "Deletes the index and forgets the chosen folders."
        Row {
            spacing: 8
            Button { theme: page.theme; visible: page.confirmErase; label: "Cancel"; onClicked: page.confirmErase = false }
            Button { theme: page.theme; danger: true; label: page.confirmErase ? "Erase" : "Erase…"; enabled: page.bridge.online
                     onClicked: { if (!page.confirmErase) { page.confirmErase = true; return }
                                  page.changeId = page.bridge.api("DELETE", "/search/index") } }
        }
    }
    Text { visible: page.message !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
           text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13 }
}
