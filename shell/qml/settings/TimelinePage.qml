import QtQuick
import Quickshell
import Quickshell.Io

// Settings, Timeline (feature 1, Undo timeline): everything that changed,
// newest first, from the activity journal: settings, apps, what Nexus read,
// what left the computer. A personal setting has Undo. Below, the versions of
// the system itself (NixOS generations): an earlier one is chosen in the boot
// menu. Files are never undone here; that is backups' job.
Column {
    id: page
    property var theme
    property var host
    readonly property var bridge: host.bridge
    width: parent ? parent.width : 600
    spacing: 10

    property var entries: []
    property var versions: []
    property string message: ""
    property int listId: -1
    property int undoId: -1
    property var undoing: null
    function refresh() { if (bridge.online) listId = bridge.api("GET", "/journal?limit=150") }
    Component.onCompleted: refresh()
    Connections {
        target: page.bridge
        function onResult(id, type, data) {
            if (id === page.listId) page.entries = data || [];
            else if (id === page.undoId) {
                // An app install or removal: Apps (or apt in a terminal) runs the opposite change and
                // reports it, which marks this entry undone (Core: AppChanges.cs).
                if (data.perform) {
                    page.host.appChange(data.perform);
                    page.message = (data.summary || "Undoing") + ". It is marked undone here once it has finished.";
                } else {
                    page.host.restore(data.ref, data.restore);
                    page.message = "Undone: " + page.undoing.summary;
                }
                page.refresh();
            }
        }
        function onFailed(id, message) { if (id === page.listId || id === page.undoId) page.message = message }
    }
    // The system's own versions: /nix/var/nix/profiles/system-N-link, newest first.
    Process {
        running: true
        command: ["sh", "-c", "cur=$(readlink /nix/var/nix/profiles/system); for l in $(ls -1v /nix/var/nix/profiles/ | grep -E '^system-[0-9]+-link$' | tac | head -12); do " +
                  "n=${l#system-}; n=${n%-link}; d=$(stat -c %Y /nix/var/nix/profiles/$l); c=0; [ \"$l\" = \"$cur\" ] && c=1; echo \"$n $d $c\"; done"]
        stdout: StdioCollector {
            onStreamFinished: page.versions = text.trim().split("\n").filter(l => l).map(l => {
                const [n, d, c] = l.split(" ");
                return { number: Number(n), date: new Date(Number(d) * 1000), current: c === "1" };
            })
        }
    }
    readonly property var labels: ({ setting: "Setting", app: "App", system: "System", "ai-read": "Nexus read", egress: "Left this computer", device: "Device" })
    readonly property var dots: ({ setting: "#FFA65C", app: "#9C8FFF", system: "#7FE0B0", "ai-read": "#86A8FF", egress: "#FF8FA3", device: "#9AA9C9" })
    function day(t) {
        const d = new Date(t), today = new Date();
        const y = new Date(); y.setDate(today.getDate() - 1);
        if (d.toDateString() === today.toDateString()) return "Today";
        if (d.toDateString() === y.toDateString()) return "Yesterday";
        return Qt.formatDate(d, "dddd d MMMM");
    }

    PageHeader { theme: page.theme; title: "Timeline"
                 summary: "Everything that changed, newest first. Personal settings and app installs and removals can be undone; your files are never touched here." }

    Text { visible: page.message !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
           text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13 }
    Text { visible: page.entries.length === 0; color: page.theme.inkSecondary; font.pixelSize: 13
           text: page.bridge.online ? "Nothing recorded yet." : "Nexus Core is not running, so the timeline cannot be read." }

    Repeater {
        model: page.entries
        delegate: Column {
            required property var modelData
            required property int index
            width: page.width; spacing: 6
            Text {
                visible: index === 0 || page.day(page.entries[index - 1].time) !== page.day(modelData.time)
                topPadding: index === 0 ? 0 : 10
                text: page.day(modelData.time); color: page.theme.inkSecondary; font.pixelSize: 12; font.weight: 600
            }
            Rectangle {
                width: parent.width; height: 48; radius: 10; color: page.theme.chip
                Rectangle { x: 14; anchors.verticalCenter: parent.verticalCenter; width: 8; height: 8; radius: 4
                            color: page.dots[modelData.kind] || "#9AA9C9" }
                Column {
                    x: 32; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 160
                    Text { width: parent.width; elide: Text.ElideRight; textFormat: Text.PlainText; text: modelData.summary
                           color: modelData.undoneAt ? page.theme.inkSecondary : page.theme.ink; font.pixelSize: 13
                           font.strikeout: !!modelData.undoneAt }
                    Text { text: Qt.formatTime(new Date(modelData.time), "HH:mm") + " · " + (page.labels[modelData.kind] || modelData.kind) +
                                 (modelData.actor === "nexus" ? " · by Nexus" : "") + (modelData.undoneAt ? " · undone" : "")
                           color: page.theme.inkSecondary; font.pixelSize: 11 }
                }
                Button {
                    anchors { right: parent.right; rightMargin: 10; verticalCenter: parent.verticalCenter }
                    theme: page.theme; label: "Undo"
                    visible: modelData.reversible === true && !modelData.undoneAt &&
                             (modelData.kind === "app" || (modelData.before !== null && modelData.before !== undefined))
                    onClicked: { page.undoing = modelData; page.undoId = page.bridge.api("POST", "/journal/" + modelData.id + "/undo") }
                }
            }
        }
    }

    Text { text: "Versions of the system"; color: page.theme.ink; font.pixelSize: 15; font.weight: 600; topPadding: 14 }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 12
        text: "Every system change makes a new version and keeps the old ones. To go back, restart and choose the version in the boot menu; " +
              "nothing is deleted, so you can come forward again the same way."
    }
    Repeater {
        model: page.versions
        delegate: SettingRow {
            required property var modelData
            theme: page.theme
            label: "Version " + modelData.number + (modelData.current ? " · running now" : "")
            detail: Qt.formatDateTime(modelData.date, "dddd d MMMM yyyy, HH:mm")
        }
    }
}
