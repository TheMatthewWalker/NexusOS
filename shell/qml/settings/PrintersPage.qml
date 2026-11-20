import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/printers.mjs" as P

// Settings, Printers: the printers CUPS knows or has found on the network
// (they appear by themselves: nix/modules/printing.nix), which one is the
// default, a test page, and scanning. lpstat, lpoptions and lp run with
// argument lists; a printer's name is checked before it is passed on.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    property var printers: []
    property string defaultPrinter: ""
    property var printerStates: ({})
    property string message: ""
    function refresh() { list.running = true; def.running = true; state.running = true }
    Component.onCompleted: refresh()
    Timer { interval: 8000; repeat: true; running: page.visible; onTriggered: page.refresh() }

    Process { id: list; command: ["lpstat", "-e"]; stdout: StdioCollector { onStreamFinished: page.printers = P.destinations(text) } }
    Process { id: def; command: ["lpstat", "-d"]; stdout: StdioCollector { onStreamFinished: page.defaultPrinter = P.defaultDestination(text) } }
    Process { id: state; command: ["lpstat", "-p"]; stdout: StdioCollector { onStreamFinished: page.printerStates = P.states(text) } }
    Process { id: setDefault; onExited: page.refresh() }
    Process {
        id: print
        property string body: ""
        stdinEnabled: true
        onStarted: { write(body); stdinEnabled = false }
        onExited: code => { stdinEnabled = true; page.message = code === 0 ? "The test page is on its way." : "The test page could not be sent." }
    }
    function testPage(name) {
        if (!P.validName(name) || print.running) return;
        print.body = P.testPage(name, new Date().toLocaleString());
        print.command = ["lp", "-d", name, "-t", "NexusOS test page"];
        print.running = true;
    }

    PageHeader { theme: page.theme; title: "Printers"; summary: "Printers and scanners on your network or plugged in appear by themselves." }

    Text {
        visible: page.message !== ""
        width: page.width; wrapMode: Text.Wrap; text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13
    }
    SettingRow {
        theme: page.theme; label: "Printers"; highlighted: page.host.highlight === "printers"
        detail: page.printers.length === 0
            ? "None found yet. Switch the printer on and connect it to the same network (or plug it in with USB); it appears here within a minute."
            : "The default is used when an app does not ask. Most printers need nothing set up."
    }
    Repeater {
        model: page.printers
        delegate: Rectangle {
            required property var modelData
            readonly property bool isDefault: modelData === page.defaultPrinter
            width: page.width; height: 52; radius: 10
            color: page.theme.chip
            border.color: isDefault ? page.theme.focus : "transparent"
            Column {
                x: 16; anchors.verticalCenter: parent.verticalCenter; spacing: 2
                Text { text: P.displayName(modelData); color: page.theme.ink; font.pixelSize: 14; textFormat: Text.PlainText }
                Text { text: P.stateLabel(page.printerStates[modelData]) + (isDefault ? " · default" : ""); color: page.theme.inkSecondary; font.pixelSize: 12 }
            }
            Row {
                anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
                spacing: 8
                Button { theme: page.theme; visible: !isDefault; label: "Make default"
                         onClicked: if (P.validName(modelData)) { setDefault.command = ["lpoptions", "-d", modelData]; setDefault.running = true } }
                Button { theme: page.theme; label: "Print a test page"; onClicked: page.testPage(modelData) }
            }
        }
    }
    SettingRow {
        theme: page.theme; label: "Scanning"; highlighted: page.host.highlight === "scanning"
        detail: "Document Scanner finds scanners and all-in-one printers on your network or plugged in."
        Button { theme: page.theme; label: "Scan a document"; onClicked: Quickshell.execDetached(["simple-scan"]) }
    }
    SettingRow {
        theme: page.theme; label: "Something unusual"; highlighted: page.host.highlight === "printeradvanced"
        detail: "An older printer that needs a driver, a shared office printer, or printer options: the printer settings tool has them all."
        Button { theme: page.theme; label: "Open printer settings"; onClicked: Quickshell.execDetached(["system-config-printer"]) }
    }
}
