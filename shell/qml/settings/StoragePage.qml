import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/storage.mjs" as S

// Settings, Storage: how full the disk is, what is using it, and ways to make
// room that are always safe: the trash, apps' caches, and earlier system
// versions older than two weeks (the one in use and the two newest stay, for
// Earlier versions). nexus-storage runs as you; removing system versions runs
// as root through pkexec (nix/modules/storage.nix).
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    property var report: ({})
    property string message: ""
    property bool messageIsError: false
    readonly property var disk: (report.volumes || [])[0]
    readonly property var home: (report.volumes || [])[1]

    function say(text, error) { message = text; messageIsError = !!error }
    function refresh() { if (!reporter.running) reporter.running = true }
    Component.onCompleted: refresh()

    Process { id: reporter; command: ["nexus-storage", "report"]; stdout: StdioCollector { onStreamFinished: page.report = S.parse(text) } }
    Process {
        id: worker
        stdout: StdioCollector { id: workerOut }
        onExited: code => {
            const r = S.parse(workerOut.text);
            if (r.error || code !== 0) page.say(r.error || "That was not done (the password was not given).", true);
            else page.say((r.message || "Done.") + S.freed(r), false);
            page.refresh();
        }
    }
    function work(command, saying) {
        if (worker.running) return;
        worker.command = command;
        worker.running = true;
        say(saying, false);
    }

    PageHeader { theme: page.theme; title: "Storage"; summary: page.disk ? S.diskLine(page.disk) : "Measuring…" }

    Text {
        visible: page.message !== ""
        width: page.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.message; color: page.messageIsError ? page.theme.error : page.theme.inkSecondary; font.pixelSize: 13
    }

    // The disk as a bar: each kind of file its own share.
    Rectangle {
        visible: !!page.disk
        width: page.width; height: 14; radius: 7; clip: true
        color: page.theme.chip
        Row {
            Repeater {
                model: page.disk ? S.shares(page.report.kinds, page.disk.totalBytes) : []
                delegate: Rectangle {
                    required property var modelData
                    required property int index
                    width: Math.max(2, modelData.share * page.width); height: 14
                    color: ["#86A8FF", "#9C8FFF", "#7FE0B0", "#FFA65C", "#FF8FA3", "#9AA9C9"][index % 6]
                }
            }
        }
    }
    SettingRow {
        visible: !!page.disk && S.low(page.disk)
        theme: page.theme; label: "Nearly full"
        detail: "Apps can stop working when the disk is full. The ways below free space without touching your files."
    }
    Repeater {
        model: page.disk ? S.shares(page.report.kinds, page.disk.totalBytes) : []
        delegate: SettingRow {
            required property var modelData
            theme: page.theme; label: modelData.label; detail: S.sizeLabel(modelData.bytes)
            Button { theme: page.theme; label: "Open"; visible: modelData.label !== "Caches"
                     onClicked: page.host.openFolder(modelData.path) }
        }
    }
    SettingRow {
        visible: !!page.home
        theme: page.theme; label: "Home folder disk"; detail: page.home ? S.diskLine(page.home) : ""
    }

    SettingRow {
        theme: page.theme; label: "Empty the trash"; highlighted: page.host.highlight === "trash"
        detail: "Files you deleted stay in the trash until it is emptied."
        Button { theme: page.theme; label: "Empty"; onClicked: page.work(["nexus-storage", "empty-trash"], "Emptying the trash…") }
    }
    SettingRow {
        theme: page.theme; label: "Clear caches"; highlighted: page.host.highlight === "caches"
        detail: "Copies apps keep to be faster (web pages, thumbnails). Nothing of yours; apps make them again as needed."
        Button { theme: page.theme; label: "Clear"; onClicked: page.work(["nexus-storage", "clear-cache"], "Clearing caches…") }
    }
    SettingRow {
        theme: page.theme; label: "Earlier system versions"; highlighted: page.host.highlight === "systemversions"
        detail: (page.report.systemVersions ? page.report.systemVersions + " kept. " : "") +
            "Each update keeps the version before it, so you can go back. Versions older than two weeks can go; the one in use and the two newest always stay."
        Button {
            theme: page.theme; label: "Remove old versions"
            onClicked: page.work([Quickshell.env("NEXUS_PKEXEC") || "/run/wrappers/bin/pkexec", "/run/current-system/sw/bin/nexus-storage-clean"],
                                 "Removing earlier versions; this can take a few minutes…")
        }
    }
}
