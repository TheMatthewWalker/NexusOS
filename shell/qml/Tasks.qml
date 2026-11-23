import QtQuick
import Quickshell
import Quickshell.Io
import "settings"
import "lib/tasks.mjs" as T

// Task manager (Ctrl+Shift+Esc, as on Windows): the apps that are open and
// what each uses (its window's process and everything it started), the
// person's other processes, and the whole computer's processor and memory.
// End task asks a program to stop (SIGTERM) and, if it has not after two
// seconds, makes it (SIGKILL). Only the person's own processes can be ended
// here; nothing runs as root. Read from /proc and `hyprctl clients` only
// while the window is open.
NexusWindow {
    id: win
    property var theme
    title: "Task manager"
    visible: false
    implicitWidth: 760; implicitHeight: 620
    minimumSize: Qt.size(560, 420)
    color: "#05071A"

    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    property int uid: -1
    property var prev: null
    property real prevTime: 0
    property var snap: null
    property var use: null
    property var clients: []
    property string tab: "apps"
    property string message: ""
    readonly property var appList: snap && use ? T.apps(clients, snap, use) : []
    readonly property var backgroundList: snap && use ? T.background(snap, use, uid, appList.flatMap(a => a.pids), 60) : []

    function open() { visible = true; refresh() }
    function refresh() { if (!reader.running) reader.running = true; if (!clientReader.running) clientReader.running = true }
    Timer { interval: 1500; repeat: true; running: win.visible; onTriggered: win.refresh() }

    Process { running: true; command: ["id", "-u"]; stdout: StdioCollector { onStreamFinished: win.uid = parseInt(text) } }
    Process {
        id: reader
        command: ["sh", "-c", "cat /proc/[0-9]*/stat 2>/dev/null; echo ===; stat -c '%u %n' /proc/[0-9]* 2>/dev/null; echo ===; " +
                              "head -1 /proc/stat; echo ===; cat /proc/meminfo; echo ===; getconf PAGESIZE; getconf CLK_TCK; nproc; " +
                              "echo ===; cat /proc/diskstats; echo ===; cat /sys/class/drm/card*/device/gpu_busy_percent 2>/dev/null; " +
                              "command -v nvidia-smi >/dev/null && nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits 2>/dev/null; true"]
        stdout: StdioCollector {
            onStreamFinished: {
                const now = Date.now(), s = T.parseSnapshot(text);
                win.use = T.usage(win.prev, s, win.prevTime ? (now - win.prevTime) / 1000 : 1);
                win.prev = s; win.prevTime = now; win.snap = s;
            }
        }
    }
    Process { id: clientReader; command: [win.hyprctl, "clients", "-j"]
              stdout: StdioCollector { onStreamFinished: { try { win.clients = JSON.parse(text) } catch (e) {} } } }

    // End task: politely, then firmly. pids are numbers checked to be the person's.
    Process { id: ender }
    Process { id: finisher }
    property var ending: []
    Timer { id: finish; interval: 2000; onTriggered: { if (win.ending.length) { finisher.command = ["kill", "-KILL"].concat(win.ending.map(String)); finisher.running = true } win.ending = [] } }
    function end(name, pids) {
        const own = pids.filter(p => Number.isInteger(p) && T.canEnd(snap, p, uid));
        if (own.length === 0) return;
        ender.command = ["kill", "-TERM"].concat(own.map(String));
        ender.running = true;
        ending = own; finish.restart();
        message = name + " was asked to stop.";
    }

    Column {
        anchors { fill: parent; margins: 22 }
        spacing: 12

        Text { text: "Task manager"; color: win.theme.ink; font.pixelSize: 22; font.weight: 600 }
        Row {
            id: meters
            width: parent.width; spacing: 18
            // Graphics only where the driver says how busy it is (AMD, Intel, NVIDIA with nvidia-smi).
            readonly property var items: win.use ? [
                { label: "Processor", value: win.use.cpu / 100, text: T.percent(win.use.cpu) + " of " + (win.snap ? win.snap.cores : 1) + " cores" },
                { label: "Memory", value: win.use.memoryTotal ? win.use.memoryUsed / win.use.memoryTotal : 0,
                  text: T.bytes(win.use.memoryUsed) + " of " + T.bytes(win.use.memoryTotal) },
                { label: "Disk", value: Math.min(1, (win.use.disk.read + win.use.disk.written) / 200e6),
                  text: T.rate(win.use.disk.read) + " read, " + T.rate(win.use.disk.written) + " written" }
            ].concat(win.use.gpu === null || win.use.gpu === undefined ? [] : [
                { label: "Graphics", value: win.use.gpu / 100, text: T.percent(win.use.gpu) + " busy" }
            ]) : []
            Repeater {
                model: meters.items
                delegate: Column {
                    required property var modelData
                    width: (meters.width - meters.spacing * (meters.items.length - 1)) / Math.max(1, meters.items.length); spacing: 4
                    Text { text: modelData.label; color: win.theme.inkSecondary; font.pixelSize: 12 }
                    Rectangle {
                        width: parent.width; height: 8; radius: 4; color: win.theme.chip
                        Rectangle { width: parent.width * Math.min(1, modelData.value); height: 8; radius: 4
                                    color: modelData.value > 0.9 ? "#FF8FA3" : win.theme.primary }
                    }
                    Text { width: parent.width; elide: Text.ElideRight; text: modelData.text; color: win.theme.ink; font.pixelSize: 12 }
                }
            }
        }
        Choice {
            theme: win.theme; name: "Show"; current: win.tab
            options: [{ value: "apps", label: "Apps (" + win.appList.length + ")" }, { value: "background", label: "Background processes" }]
            onChose: v => win.tab = v
        }
        Text { visible: win.message !== ""; text: win.message; color: win.theme.inkSecondary; font.pixelSize: 12 }
        Row {
            width: parent.width
            Text { width: parent.width - 260; text: "Name"; color: win.theme.inkSecondary; font.pixelSize: 12 }
            Text { width: 80; text: "Processor"; color: win.theme.inkSecondary; font.pixelSize: 12 }
            Text { width: 80; text: "Memory"; color: win.theme.inkSecondary; font.pixelSize: 12 }
        }
        ListView {
            width: parent.width; height: parent.height - y
            clip: true; spacing: 4
            model: win.tab === "apps" ? win.appList : win.backgroundList
            delegate: Rectangle {
                required property var modelData
                width: ListView.view.width; height: 44; radius: 10
                color: win.theme.chip
                Column {
                    x: 12; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 272
                    Text { width: parent.width; elide: Text.ElideRight; text: modelData.name; color: win.theme.ink; font.pixelSize: 13; textFormat: Text.PlainText }
                    Text { visible: !!modelData.title; width: parent.width; elide: Text.ElideRight; text: modelData.title || ""
                           color: win.theme.inkSecondary; font.pixelSize: 11; textFormat: Text.PlainText }
                }
                Text { x: parent.width - 260; anchors.verticalCenter: parent.verticalCenter; width: 80
                       text: T.percent(modelData.cpu); color: modelData.cpu > 50 ? "#FFA65C" : win.theme.ink; font.pixelSize: 13 }
                Text { x: parent.width - 180; anchors.verticalCenter: parent.verticalCenter; width: 80
                       text: T.bytes(modelData.memory); color: win.theme.ink; font.pixelSize: 13 }
                Button {
                    anchors { right: parent.right; rightMargin: 8; verticalCenter: parent.verticalCenter }
                    theme: win.theme; label: "End task"
                    onClicked: win.end(modelData.name, modelData.pids || [modelData.pid])
                }
            }
        }
    }
}
