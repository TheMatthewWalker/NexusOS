import QtQuick
import Quickshell
import Quickshell.Io
import "settings"
import "installer"
import "lib/installer.mjs" as I

// The graphical installer, on the live USB (live.qml opens it at start and
// from its Install NexusOS button). Steps: Welcome, Disk, You, Time zone,
// Summary, Install, Done. It installs nothing itself: it runs nexus-install
// (nix/installer/nexus-install.sh) as root through pkexec, with flags, first
// with --probe-json (the disks, what could go alongside on each, the hardware
// scan) and then with --progress-json, reading one JSON object per step. The
// password goes to nexus-install on standard input, never on its command line.
// The live image's polkit rule lets the live user run exactly that program
// without a password (nix/installer/iso.nix). The terminal installer
// (nexus-install-welcome) stays available from the live screen.
NexusWindow {
    id: win
    property var theme
    title: "Install NexusOS"
    visible: false
    implicitWidth: 1040; implicitHeight: 720
    minimumSize: Qt.size(820, 600)
    color: "#05071A"

    readonly property string tool: Quickshell.env("NEXUS_INSTALL") || "/run/current-system/sw/bin/nexus-install"
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"

    property string step: "welcome"
    property var probe: null
    property bool probing: false
    property string probeError: ""
    property var zones: []
    // The choices.
    property string disk: ""
    property bool alongside: false
    property bool encrypt: true
    property string fullName: ""
    property string login: ""
    property bool loginEdited: false
    property string password: ""
    property string again: ""
    property string host: "nexus"
    property string timezone: "UTC"
    property var progress: I.initialProgress()

    readonly property var diskInfo: probe ? (probe.disks.find(d => d.path === disk) || null) : null
    readonly property var choices: I.diskChoices(diskInfo, probe ? probe.minimumBytes : 0)
    readonly property var problems: I.youProblems({ fullName: fullName, login: login, password: password, again: again, host: host })
    readonly property int stepIndex: I.STEPS.indexOf(step)

    function open() {
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Install NexusOS)$"]);
        visible = true;
        if (!probe && !probing) runProbe();
        if (!zones.length) zoneList.running = true;
    }
    function runProbe() { probing = true; probeError = ""; probeProc.running = true }
    function chooseDisk(path) {
        disk = path;
        // Alongside first when it is possible: it keeps what is there.
        alongside = choices.alongside.possible;
    }
    function setFullName(text) {
        fullName = text;
        if (!loginEdited) login = I.suggestLogin(text);
    }
    function summaryState() {
        return { disk: disk, diskInfo: diskInfo, alongside: alongside, systems: choices.alongside.systems,
                 freeBytes: choices.alongside.freeBytes, encrypt: encrypt, fullName: fullName, login: login,
                 host: host, timezone: timezone };
    }
    function canNext() {
        switch (step) {
            case "welcome": return !!probe;
            case "disk": return disk !== "" && (alongside ? choices.alongside.possible : choices.erase.possible);
            case "you": return I.youReady({ fullName: fullName, login: login, password: password, again: again, host: host });
            case "time": return timezone !== "";
            default: return false;
        }
    }
    function next() {
        if (step === "summary") { startInstall(); return }
        if (!canNext()) return;
        step = I.STEPS[stepIndex + 1];
    }
    function back() { if (stepIndex > 0 && step !== "install" && step !== "done") step = I.STEPS[stepIndex - 1] }

    function startInstall() {
        progress = I.applyEvent(I.initialProgress(), { event: "start" });
        step = "install";
        installErr.lastLine = "";
        installProc.command = ["pkexec", tool].concat(I.installArgs(summaryState()));
        installProc.running = true;
    }

    Process {
        id: probeProc
        command: ["pkexec", win.tool, "--probe-json"]
        stdout: StdioCollector { id: probeOut }
        stderr: StdioCollector { id: probeErr }
        // A program that cannot start never exits: say so instead of looking for ever.
        onRunningChanged: if (!running) Qt.callLater(() => {
            if (win.probing) { win.probing = false; win.probeError = I.exitProblem(127, "could not start pkexec") }
        })
        onExited: code => {
            win.probing = false;
            const events = probeOut.text.split("\n").map(I.parseLine).filter(e => e);
            const found = events.find(e => e.event === "probe");
            const error = events.find(e => e.event === "error");
            if (found) {
                win.probe = found;
                if (found.timezone) win.timezone = found.timezone;
                const usable = found.disks.filter(d => !d.inUse);
                if (usable.length === 1 && win.disk === "") win.chooseDisk(usable[0].path);
            } else {
                win.probeError = error ? error.message : I.exitProblem(code, probeErr.text.trim().split("\n").pop() || "");
            }
        }
    }
    Process {
        id: zoneList
        command: ["timedatectl", "list-timezones"]
        stdout: StdioCollector { onStreamFinished: win.zones = text.split("\n").filter(z => z !== "") }
    }
    Process {
        id: installProc
        stdinEnabled: true
        // The password, then close standard input (nexus-install reads one line).
        onRunningChanged: {
            if (running) { write(win.password + "\n"); stdinEnabled = false; return }
            Qt.callLater(() => {
                if (win.progress.phase === "running" && !installProc.running)
                    win.progress = I.applyEvent(win.progress, { event: "error", message: I.exitProblem(127, "could not start pkexec") });
            });
        }
        stdout: SplitParser {
            onRead: line => {
                const ev = I.parseLine(line);
                if (ev) win.progress = I.applyEvent(win.progress, ev);
            }
        }
        stderr: SplitParser { id: installErr; property string lastLine: ""; onRead: line => { if (line.trim()) lastLine = line.trim() } }
        onExited: code => {
            stdinEnabled = true;
            if (win.progress.phase === "done") { win.step = "done"; win.password = ""; win.again = ""; return }
            if (win.progress.phase !== "failed")
                win.progress = I.applyEvent(win.progress, { event: "error", message: I.exitProblem(code, installErr.lastLine), log: "/tmp/nexus-install.log" });
        }
    }

    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#0B0F24" }
            GradientStop { position: 1; color: "#03040C" }
        }
    }

    // The steps, down the left.
    Column {
        id: rail
        x: 32; y: 40; width: 170
        spacing: 6
        Text { text: "NexusOS"; color: win.theme.inkSecondary; font.pixelSize: 13; font.weight: 600; font.letterSpacing: 2; bottomPadding: 18 }
        Repeater {
            model: I.STEPS
            delegate: Row {
                required property var modelData
                required property int index
                spacing: 10
                height: 30
                Rectangle {
                    anchors.verticalCenter: parent.verticalCenter
                    width: 10; height: 10; radius: 5
                    color: index < win.stepIndex ? win.theme.primary : index === win.stepIndex ? win.theme.focus : "transparent"
                    border.color: index <= win.stepIndex ? "transparent" : win.theme.glassBorder
                }
                Text {
                    anchors.verticalCenter: parent.verticalCenter
                    text: I.STEP_LABELS[modelData]
                    color: index === win.stepIndex ? win.theme.ink : win.theme.inkSecondary
                    font.pixelSize: 14; font.weight: index === win.stepIndex ? 600 : 400
                }
            }
        }
    }

    // The current step.
    Flickable {
        id: body
        anchors { left: rail.right; leftMargin: 32; right: parent.right; rightMargin: 40; top: parent.top; topMargin: 40; bottom: nav.top; bottomMargin: 16 }
        contentHeight: pages.implicitHeight
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        Item {
            id: pages
            width: body.width
            implicitHeight: childrenRect.height

            // Welcome.
            Column {
                visible: win.step === "welcome"
                width: parent.width; spacing: 14
                Text { text: "Install NexusOS"; color: win.theme.ink; font.pixelSize: 30; font.weight: 600 }
                Text {
                    width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 15; lineHeight: 1.3
                    text: "This puts NexusOS on this computer's disk. You choose the disk next: NexusOS can use a whole disk, " +
                          "or go alongside Windows in free space you have made in Windows. Nothing changes until you press Install on the summary.\n\n" +
                          "No internet connection is needed: everything is on the USB stick."
                }
                Text {
                    width: parent.width; wrapMode: Text.Wrap; font.pixelSize: 13
                    color: win.probeError ? win.theme.error : win.theme.inkSecondary
                    text: win.probing ? "Looking at this computer…" : win.probeError
                        ? win.probeError : win.probe ? "Found " + win.probe.disks.length + " disk" + (win.probe.disks.length === 1 ? "" : "s") + ". " + win.probe.hardware.graphics + "." : ""
                }
                Button { visible: win.probeError !== "" && !win.probing; theme: win.theme; label: "Look again"; onClicked: win.runProbe() }
            }
            DiskPage { visible: win.step === "disk"; width: parent.width; app: win; theme: win.theme }
            YouPage { id: youPage; visible: win.step === "you"; width: parent.width; app: win; theme: win.theme }
            TimePage { visible: win.step === "time"; width: parent.width; app: win; theme: win.theme }
            SummaryPage { visible: win.step === "summary"; width: parent.width; app: win; theme: win.theme }
            ProgressPage { visible: win.step === "install"; width: parent.width; app: win; theme: win.theme }
            // Done.
            Column {
                visible: win.step === "done"
                width: parent.width; spacing: 14
                Text { text: "NexusOS is installed"; color: win.theme.ink; font.pixelSize: 30; font.weight: 600 }
                Text {
                    width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 15; lineHeight: 1.3
                    text: "Restart the computer and remove the USB stick when the screen goes dark." +
                          (win.encrypt ? " When it starts, type your password to unlock the disk, then again to log in." : "") +
                          (win.alongside ? " The start-up menu lists NexusOS and Windows; NexusOS starts unless you choose Windows." : "")
                }
            }
        }
    }
    onStepChanged: { body.contentY = 0; if (step === "you") youPage.focusFirst() }

    // Back and Next, along the bottom.
    Item {
        id: nav
        anchors { left: rail.right; leftMargin: 32; right: parent.right; rightMargin: 40; bottom: parent.bottom; bottomMargin: 28 }
        height: 40
        Button {
            anchors { left: parent.left; verticalCenter: parent.verticalCenter }
            visible: win.stepIndex > 0 && win.step !== "done" && (win.step !== "install" || win.progress.phase === "failed")
            theme: win.theme; label: win.step === "install" ? "Back to the summary" : "Back"
            onClicked: { if (win.step === "install") win.step = "summary"; else win.back() }
        }
        Row {
            anchors { right: parent.right; verticalCenter: parent.verticalCenter }
            spacing: 10
            Button {
                visible: win.step === "install" && win.progress.phase === "failed"
                theme: win.theme; primary: true; label: "Try again"
                onClicked: win.startInstall()
            }
            Button {
                visible: win.step !== "install" && win.step !== "done"
                enabled: win.step === "summary" || win.canNext()
                theme: win.theme; primary: true
                danger: win.step === "summary" && !win.alongside
                label: win.step === "summary" ? (win.alongside ? "Install" : "Erase and install") : "Next"
                onClicked: win.next()
            }
            Button {
                visible: win.step === "done"
                theme: win.theme; primary: true; label: "Restart now"
                onClicked: Quickshell.execDetached(["systemctl", "reboot"])
            }
        }
    }

    IpcHandler {
        target: "installer"
        function open(): void { win.open() }
    }
}
