import QtQuick
import Quickshell
import Quickshell.Io
import "settings"
import "lib/pickers.mjs" as P

// The emoji picker (Super+., as on Windows): search by name, or browse by
// group; a click types the emoji into the window that was in front (wtype)
// and copies it too. The list is Unicode's (nix/modules/everyday.nix).
NexusWindow {
    id: win
    property var theme
    title: "Emoji"
    visible: false
    implicitWidth: 460; implicitHeight: 480
    color: "#05071A"

    property var all: []
    property string query: ""
    property string group: ""
    readonly property var shown: P.searchEmoji(all, query, query ? "" : (group || (all.length ? all[0].group : ""))).slice(0, 400)
    function open() { visible = true; search.text = ""; query = ""; search.forceActiveFocus() }

    FileView { path: "/etc/nexus/emoji-test.txt"; onLoaded: win.all = P.emoji(text()) }
    Process { id: typer }
    function choose(e) {
        visible = false;
        typer.command = ["sh", "-c", "printf %s \"$1\" | wl-copy; sleep 0.15; wtype -- \"$1\"", "emoji", e.char];
        typer.running = true;
    }

    Column {
        anchors { fill: parent; margins: 16 }
        spacing: 8
        TextField { id: search; theme: win.theme; label: "Emoji"; placeholder: "Search: heart, laugh, pizza…"; width: parent.width
                    onTextChanged: win.query = text; onAccepted: if (win.shown.length) win.choose(win.shown[0]) }
        Flow {
            visible: win.query === ""
            width: parent.width; spacing: 4
            Repeater {
                model: P.groups(win.all)
                delegate: Button { required property var modelData; theme: win.theme; label: modelData.split(" ")[0]
                                   primary: (win.group || P.groups(win.all)[0]) === modelData; onClicked: win.group = modelData }
            }
        }
        GridView {
            width: parent.width; height: parent.height - y; clip: true
            cellWidth: 44; cellHeight: 44
            model: win.shown
            delegate: Rectangle {
                required property var modelData
                width: 40; height: 40; radius: 8
                color: hover.hovered ? win.theme.chip : "transparent"
                Text { anchors.centerIn: parent; text: modelData.char; font.pixelSize: 24; font.family: "Noto Color Emoji" }
                HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: win.choose(modelData) }
                Accessible.role: Accessible.Button
                Accessible.name: modelData.name
            }
        }
    }
}
