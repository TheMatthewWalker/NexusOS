import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/backups.mjs" as B

// Settings, Backups: a copy of your files on a drive, made once a day by
// itself while the drive is plugged in (nix/modules/backups.nix). The work is
// done by nexus-backup, as you: restic keeps the backup encrypted with a
// backup password, so the drive is safe to lose. Restoring puts the files in
// "Restored files" in your home folder and never overwrites anything.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property string home: Quickshell.env("HOME") || ""
    property var status: ({})
    property var drives: []
    property var snapshots: []
    property string message: ""
    property bool messageIsError: false
    property bool choosing: false        // picking a drive (first time, or a new one)
    property string chosenDrive: ""
    property string restoring: ""        // the backup whose restore choices are open
    property string restoredFolder: ""

    function say(text, error) { message = text; messageIsError = !!error }
    function refresh() {
        statusReader.running = true;
        driveReader.running = true;
    }
    Component.onCompleted: refresh()
    Timer { interval: 5000; repeat: true; running: page.visible; onTriggered: page.refresh() }

    Process {
        id: statusReader
        command: ["nexus-backup", "status"]
        stdout: StdioCollector {
            onStreamFinished: {
                const s = B.parse(text);
                const wasPresent = page.status.drivePresent;
                page.status = s;
                if (s.setUp && s.drivePresent && (!wasPresent || page.snapshots.length === 0) && !snapshotReader.running) snapshotReader.running = true;
            }
        }
    }
    Process { id: driveReader; command: ["nexus-backup", "drives"]; stdout: StdioCollector { onStreamFinished: page.drives = B.parse(text).drives || [] } }
    Process { id: snapshotReader; command: ["nexus-backup", "snapshots"]; stdout: StdioCollector { onStreamFinished: page.snapshots = B.parse(text).snapshots || [] } }

    // One change at a time; the setup password goes on standard input.
    Process {
        id: worker
        property string input: ""
        property string operation: ""
        stdinEnabled: true
        stdout: StdioCollector { id: workerOut }
        onStarted: { if (input !== "") write(input + "\n"); stdinEnabled = false }
        onExited: code => {
            const r = B.parse(workerOut.text);
            input = ""; stdinEnabled = true;
            if (r.error || code !== 0) { page.say(r.error || "That did not work.", true); page.refresh(); return }
            page.say(r.message || "Done.", false);
            if (operation === "setup") { page.choosing = false; password.clear(); again.clear() }
            if (operation === "restore") { page.restoring = ""; page.restoredFolder = r.folder || "" }
            page.refresh();
            snapshotReader.running = true;
        }
    }
    function work(operation, args, input, working) {
        if (worker.running) return;
        worker.operation = operation;
        worker.input = input || "";
        worker.command = ["nexus-backup", operation].concat(args || []);
        worker.running = true;
        restoredFolder = "";
        say(working, false);
    }

    PageHeader { theme: page.theme; title: "Backups"; summary: "A copy of your files on a drive, made by itself once a day while the drive is plugged in." }

    Text {
        visible: page.message !== ""
        width: page.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.message; color: page.messageIsError ? page.theme.error : page.theme.inkSecondary; font.pixelSize: 13
    }
    Button {
        visible: page.restoredFolder !== ""
        theme: page.theme; label: "Open the restored files"
        onClicked: Quickshell.execDetached(["xdg-open", page.restoredFolder])
    }

    SettingRow {
        theme: page.theme; label: "Backups"; highlighted: page.host.highlight === "backups"
        detail: B.statusLine(page.status, new Date())
        Button {
            visible: !!page.status.setUp && !!page.status.drivePresent
            theme: page.theme; label: worker.running && worker.operation === "run" ? "Backing up…" : "Back up now"
            onClicked: page.work("run", [], "", "Backing up. The first backup takes a while; later ones only add what changed.")
        }
        Button {
            visible: !page.status.setUp && !page.choosing
            theme: page.theme; label: "Set up backups"
            onClicked: page.choosing = true
        }
    }

    // Choosing the drive, and the backup password.
    SettingRow {
        visible: page.choosing || (!!page.status.setUp && page.host.highlight === "backupdrive")
        theme: page.theme; label: "Back up to"; highlighted: page.host.highlight === "backupdrive"
        detail: page.drives.length === 0
            ? "Plug in a USB drive or an external disk (and open it in Files if it does not appear). Any drive works; your files stay encrypted on it."
            : "Choose the drive. Backups only use free space: nothing on it is changed."
    }
    Repeater {
        model: page.choosing ? page.drives : []
        delegate: Rectangle {
            required property var modelData
            width: page.width; height: 48; radius: 10
            color: page.theme.chip
            border.color: modelData.path === page.chosenDrive ? page.theme.focus : "transparent"
            Text {
                x: 16; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 32; elide: Text.ElideRight
                text: B.driveLabel(modelData); color: page.theme.ink; font.pixelSize: 14; textFormat: Text.PlainText
            }
            MouseArea { anchors.fill: parent; onClicked: page.chosenDrive = modelData.path }
        }
    }
    Column {
        visible: page.choosing && page.chosenDrive !== ""
        spacing: 8
        Text {
            width: page.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
            text: "The backup password locks the backup. Write it down and keep it somewhere other than this computer: without it, the backup cannot be opened if this computer is lost. If the drive already has a NexusOS backup, use its password."
        }
        TextField { id: password; theme: page.theme; label: "Backup password (10 characters or more)"; password: true }
        TextField { id: again; theme: page.theme; label: "Backup password again"; password: true; onAccepted: setupButton.clicked() }
        Row {
            spacing: 8
            Button {
                id: setupButton
                theme: page.theme; label: "Use this drive"
                onClicked: {
                    const problem = B.passwordProblem(password.text, again.text);
                    if (problem) { page.say(problem, true); return }
                    page.work("setup", ["--dest", page.chosenDrive], password.text, "Setting up the drive…");
                }
            }
            Button { theme: page.theme; label: "Cancel"; onClicked: { page.choosing = false; password.clear(); again.clear() } }
        }
    }

    SettingRow {
        visible: !!page.status.setUp && !page.choosing
        theme: page.theme; label: "Backup drive"; highlighted: page.host.highlight === "backupdrive"
        detail: (page.status.driveName || "") + (page.status.drivePresent ? " · plugged in" : " · not plugged in") +
            ". Backups keep each day for a week, each week for a month and each month for a year."
        Button { theme: page.theme; label: "Use another drive"; onClicked: { page.chosenDrive = ""; page.choosing = true } }
    }

    // Restoring: into ~/Restored files, never over your files.
    SettingRow {
        visible: !!page.status.setUp && !!page.status.drivePresent
        theme: page.theme; label: "Restore files"; highlighted: page.host.highlight === "restore"
        detail: page.snapshots.length === 0
            ? "No backups on the drive yet."
            : "Choose a backup and what to bring back. The files are put in Restored files in your home folder; nothing you have now is changed."
    }
    Repeater {
        model: page.status.drivePresent ? page.snapshots.slice(0, 12) : []
        delegate: Rectangle {
            required property var modelData
            readonly property bool open: page.restoring === modelData.id
            width: page.width; height: open ? 96 : 48; radius: 10
            color: page.theme.chip
            Text {
                x: 16; y: 14
                text: "Backup from " + B.whenLabel(modelData.time, new Date()); color: page.theme.ink; font.pixelSize: 14
            }
            Button {
                anchors { right: parent.right; rightMargin: 12; top: parent.top; topMargin: 8 }
                theme: page.theme; label: open ? "Close" : "Restore…"
                onClicked: page.restoring = open ? "" : modelData.id
            }
            Row {
                visible: open
                x: 16; y: 54; spacing: 8
                Repeater {
                    model: B.FOLDERS
                    delegate: Button {
                        required property var modelData
                        theme: page.theme; label: B.folderLabel(modelData)
                        onClicked: {
                            const path = B.restorePath(page.home, modelData);
                            page.work("restore", ["--snapshot", page.restoring].concat(path ? ["--path", path] : []), "",
                                      "Restoring " + B.folderLabel(modelData).toLowerCase() + "…");
                        }
                    }
                }
            }
        }
    }
}
