import QtQuick
import Quickshell
import Quickshell.Io
import "settings"

// A Windows program was opened (a .exe or .msi: nix/modules/gaming-windows.nix
// makes this the handler for them). NexusOS does not run Windows programs by
// itself; this says what the program is, offers the same app or one that does
// the same job here (Core's list, POST /windows-program), and, for anything
// else, Bottles, which runs many Windows programs with Wine.
NexusWindow {
    id: win
    property var theme
    property var bridge
    signal getApps(var list)
    signal askNexus(string text)
    title: "Windows program"
    visible: false
    implicitWidth: 560; implicitHeight: 360
    color: "#05071A"

    property string path: ""
    readonly property string fileName: path.split("/").pop()
    property string program: ""
    property var match: null
    property var game: null              // GameCheck.cs: whether a game runs here (anti-cheat)
    property int callId: -1
    function open(p) {
        path = String(p || "").replace(/^file:\/\//, "");
        program = ""; match = null; game = null; visible = true;
        if (bridge && bridge.online) callId = bridge.api("POST", "/windows-program", { fileName: fileName });
    }
    Connections {
        target: win.bridge
        function onResult(id, type, data) { if (id === win.callId) { win.program = data.program || ""; win.match = data.match || null; win.game = data.game || null } }
    }
    IpcHandler { target: "windows"; function open(path: string): void { win.open(path) } }
    Process { id: copier }

    Column {
        anchors { fill: parent; margins: 24 }
        spacing: 12
        Text { text: win.fileName; color: win.theme.ink; font.pixelSize: 18; font.weight: 600; width: parent.width; elide: Text.ElideMiddle; textFormat: Text.PlainText }
        Rectangle {
            visible: !!win.game
            width: parent.width; height: gameText.implicitHeight + 24; radius: 12
            color: win.game && win.game.status === "blocked" ? "#2A1A0C" : win.theme.chip
            border.color: win.game && win.game.status === "blocked" ? "#FFA65C" : "transparent"
            Text {
                id: gameText
                x: 14; y: 12; width: parent.width - 28; wrapMode: Text.Wrap; textFormat: Text.PlainText; font.pixelSize: 13
                color: win.game && win.game.status === "blocked" ? "#FFC48F" : win.theme.ink
                text: win.game ? win.game.message : ""
            }
        }
        Text {
            width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 13; textFormat: Text.PlainText
            text: !win.match ? "This is a program for Windows. NexusOS does not run Windows programs by itself, but most have a version or an equivalent here."
                : win.match.already ? (win.program + " is for Windows. NexusOS already has " + win.match.already + ", which does the same job.")
                : win.match.same ? (win.program + " is for Windows, and there is a version for NexusOS: " + win.match.name + ".")
                : (win.program + " is for Windows. " + win.match.name + " does the same job on NexusOS.")
        }
        Row {
            spacing: 8
            Button {
                visible: !!win.match && !!win.match.appId
                theme: win.theme; primary: true; label: win.match ? "Get " + win.match.name : ""
                onClicked: { win.getApps([{ id: win.match.appId, name: win.match.name, was: win.program, same: win.match.same }]); win.visible = false }
            }
            Button { theme: win.theme; label: "Ask Nexus"
                     onClicked: { win.askNexus("I opened " + win.fileName + ", a Windows program. What should I use on NexusOS instead, or how can I run it?"); win.visible = false } }
        }
        Text {
            width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 12
            text: "To try the Windows program itself: Bottles runs many Windows programs (with Wine). Get Bottles, make a bottle, choose Run Executable and pick this file (its location is copied when you press the button). Games are best in Steam or Heroic."
        }
        Row {
            spacing: 8
            Button { theme: win.theme; label: "Get Bottles"
                     onClicked: { win.getApps([{ id: "com.usebottles.bottles", name: "Bottles", was: win.fileName, same: false }]); win.visible = false } }
            Button { theme: win.theme; label: "Open Bottles and copy the file's location"
                     onClicked: { copier.command = ["wl-copy", "--", win.path]; copier.running = true
                                  Quickshell.execDetached(["flatpak", "run", "com.usebottles.bottles"]); win.visible = false } }
        }
    }
}
