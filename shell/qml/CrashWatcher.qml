import QtQuick
import Quickshell
import Quickshell.Io
import "lib/feedback.mjs" as F

// Crash notices, only when the person turned them on (Settings, Feedback):
// every two minutes, systemd-coredump's list of this person's crashes is read
// (coredumpctl, as them), and a new one is told with a notification. Nothing
// leaves the computer; Settings, Feedback can then turn it into a report.
Scope {
    id: watcher
    property var desktop
    property real since: Date.now()
    readonly property string home: Quickshell.env("HOME") || ""

    Timer {
        interval: 120000; repeat: true
        running: !!watcher.desktop && watcher.desktop.crashNotices
        onRunningChanged: if (running) watcher.since = Date.now()
        onTriggered: if (!reader.running) reader.running = true
    }
    Process {
        id: reader
        command: ["coredumpctl", "--json=short", "--no-pager", "list", "--since=-10min"]
        stdout: StdioCollector {
            onStreamFinished: {
                const fresh = F.newSince(F.crashes(text, watcher.home), watcher.since);
                if (fresh.length === 0) return;
                watcher.since = Math.max.apply(null, fresh.map(c => c.when));
                const c = fresh[0];
                Quickshell.execDetached(["notify-send", "--app-name=Feedback", "--icon=dialog-warning",
                    c.name + " stopped unexpectedly",
                    "You can send a report from Settings, Feedback; you see all of it first."]);
            }
        }
    }
}
