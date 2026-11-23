import QtQuick
import Quickshell
import Quickshell.Io
import "settings"
import "lib/pickers.mjs" as P

// Clipboard history (Super+Shift+V; Super+V is push-to-talk): what was copied
// recently, kept by cliphist (nix/modules/everyday.nix), on this computer
// only. Choosing an entry copies it again and pastes it into the window that
// was in front. Clear forgets it all. Ids are checked to be numbers before
// they reach a command.
NexusWindow {
    id: win
    property var theme
    title: "Clipboard"
    visible: false
    implicitWidth: 520; implicitHeight: 560
    color: "#05071A"

    property var items: []
    property string query: ""
    readonly property var shown: P.searchClips(items, query)
    function open() { visible = true; query = ""; search.text = ""; lister.running = true; search.forceActiveFocus() }

    Process { id: lister; command: ["cliphist", "list"]; stdout: StdioCollector { onStreamFinished: win.items = P.clips(text).slice(0, 100) } }
    Process { id: paster; onExited: win.items = win.items }
    Process { id: wiper; command: ["cliphist", "wipe"]; onExited: lister.running = true }
    function choose(id) {
        if (!/^\d+$/.test(id)) return;
        visible = false;
        // Copy it again, give the window behind a moment to have focus, then paste there.
        paster.command = ["sh", "-c", "cliphist decode " + id + " | wl-copy && sleep 0.15 && wtype -M ctrl v -m ctrl"];
        paster.running = true;
    }

    Column {
        anchors { fill: parent; margins: 18 }
        spacing: 10
        Row {
            spacing: 8
            TextField { id: search; theme: win.theme; label: "Clipboard history"; placeholder: "Search what you copied"
                        width: 360; onTextChanged: win.query = text; onAccepted: if (win.shown.length) win.choose(win.shown[0].id) }
            Button { anchors.bottom: parent.bottom; theme: win.theme; label: "Clear"; onClicked: wiper.running = true }
        }
        Text { visible: win.items.length === 0; text: "Nothing copied yet. What you copy shows here, newest first."
               color: win.theme.inkSecondary; font.pixelSize: 13; width: parent.width; wrapMode: Text.Wrap }
        ListView {
            width: parent.width; height: parent.height - y; clip: true; spacing: 4
            model: win.shown
            delegate: Rectangle {
                required property var modelData
                width: ListView.view.width; height: 44; radius: 10
                color: hover.hovered ? win.theme.primary : win.theme.chip
                Text { x: 12; width: parent.width - 24; anchors.verticalCenter: parent.verticalCenter; elide: Text.ElideRight
                       maximumLineCount: 1; textFormat: Text.PlainText; text: modelData.preview.replace(/\s+/g, " ")
                       color: win.theme.ink; font.pixelSize: 13; font.italic: modelData.image }
                HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: win.choose(modelData.id) }
                Accessible.role: Accessible.Button
                Accessible.name: modelData.preview
            }
        }
    }
}
