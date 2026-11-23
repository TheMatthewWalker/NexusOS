import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Hyprland
import "lib/switcher.mjs" as S

// Alt+Tab, as on Windows: while Alt is held, the open windows show in the
// middle of the screen, the last one used first; each Tab moves on
// (Shift+Tab back) and letting go of Alt switches to the one chosen. The keys
// are Hyprland binds (desktop.nix): Alt+Tab and Alt+Shift+Tab, and Alt's
// release, which does not take the key from apps. This window never takes
// the keyboard. A click on a window switches to it too.
Scope {
    id: sw
    property var theme
    property var apps                    // AppDirectory, for icons
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    property var windows: []
    property int index: -1
    property bool open: false
    property int pending: 0              // Tab presses before the window list arrived

    Process {
        id: reader
        command: [sw.hyprctl, "clients", "-j"]
        stdout: StdioCollector {
            onStreamFinished: {
                let list = [];
                try { list = S.order(JSON.parse(text)) } catch (e) {}
                sw.windows = list;
                if (list.length < 2) { sw.open = false; sw.pending = 0; return }   // nothing to switch to
                let i = S.first(list.length, sw.pending);
                for (let n = Math.abs(sw.pending) - 1; n > 0; n--) i = S.step(i, list.length, sw.pending);
                sw.index = i; sw.pending = 0;
            }
        }
    }
    function press(direction) {
        if (!open) { open = true; index = -1; pending = direction; if (!reader.running) reader.running = true; idle.restart(); return }
        idle.restart();
        if (index < 0) { pending += direction; return }
        index = S.step(index, windows.length, direction);
    }
    function done() {
        if (!open) return;
        open = false; idle.stop();
        const w = windows[index];
        if (w) { Hyprland.dispatch("focuswindow address:" + w.address); Hyprland.dispatch("bringactivetotop") }
        index = -1;
    }
    function choose(i) { index = i; done() }
    // If Alt's release never arrives (it was let go over a screen lock, say), switch anyway.
    Timer { id: idle; interval: 4000; onTriggered: sw.done() }

    GlobalShortcut { appid: "nexus"; name: "switchnext"; onPressed: sw.press(1) }
    GlobalShortcut { appid: "nexus"; name: "switchprev"; onPressed: sw.press(-1) }
    GlobalShortcut { appid: "nexus"; name: "switchdone"; onPressed: sw.done() }

    PanelWindow {
        visible: sw.open && sw.index >= 0
        color: "transparent"
        implicitWidth: Math.min(row.implicitWidth + 32, 1100); implicitHeight: 170
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.layer: WlrLayer.Overlay
        WlrLayershell.namespace: "nexus-switcher"
        WlrLayershell.keyboardFocus: WlrKeyboardFocus.None
        Rectangle {
            anchors.fill: parent; radius: 22
            color: sw.theme ? sw.theme.glass : "#e60c142a"
            border.color: sw.theme ? sw.theme.glassBorder : "#33ffffff"
            ListView {
                id: row
                anchors { fill: parent; margins: 16 }
                orientation: ListView.Horizontal; spacing: 10; clip: true
                implicitWidth: count * 150 + Math.max(0, count - 1) * 10
                model: sw.windows
                currentIndex: sw.index
                highlightFollowsCurrentItem: true
                delegate: Rectangle {
                    required property var modelData
                    required property int index
                    width: 150; height: row.height; radius: 14
                    color: index === sw.index ? Qt.rgba(0.49, 0.42, 1.0, 0.32) : hover.hovered ? Qt.rgba(1, 1, 1, 0.08) : "transparent"
                    border.color: index === sw.index && sw.theme ? sw.theme.focus : "transparent"
                    border.width: 2
                    Column {
                        anchors.centerIn: parent; width: parent.width - 16; spacing: 10
                        Image {
                            anchors.horizontalCenter: parent.horizontalCenter
                            width: 56; height: 56; sourceSize: Qt.size(112, 112)
                            source: sw.apps ? sw.apps.iconFor(modelData.appId) : ""
                        }
                        Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; elide: Text.ElideRight; maximumLineCount: 2
                               wrapMode: Text.Wrap; textFormat: Text.PlainText; text: modelData.title
                               color: sw.theme ? sw.theme.ink : "white"; font.pixelSize: 12 }
                    }
                    HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
                    TapHandler { onTapped: sw.choose(index) }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Switch to " + modelData.title
                }
            }
        }
    }
}
