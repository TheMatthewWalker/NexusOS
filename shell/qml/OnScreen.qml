import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Hyprland
import Quickshell.Services.Pipewire
import "lib/onscreen.mjs" as O

// The volume, mute, microphone and brightness keys (bound in desktop.nix to
// global shortcuts): each press changes the setting and shows how much, at the
// bottom of the screen, for a moment, as Windows does. Caps Lock too.
Scope {
    id: osd
    property var theme
    readonly property var sink: Pipewire.defaultAudioSink
    readonly property var source: Pipewire.defaultAudioSource
    PwObjectTracker { objects: [osd.sink, osd.source].filter(x => x) }

    property string kind: "volume"
    property real value: 0
    property bool muted: false
    function show(k, v, m) { kind = k; value = v; muted = !!m; card.visible = true; hide.restart() }
    Timer { id: hide; interval: 1500; onTriggered: card.visible = false }

    function volume(up) {
        if (!sink || !sink.audio) return;
        sink.audio.muted = false;
        sink.audio.volume = O.step(sink.audio.volume, up);
        show("volume", sink.audio.volume, false);
    }
    GlobalShortcut { appid: "nexus"; name: "volup"; onPressed: osd.volume(true) }
    GlobalShortcut { appid: "nexus"; name: "voldown"; onPressed: osd.volume(false) }
    GlobalShortcut { appid: "nexus"; name: "mute"
        onPressed: if (osd.sink && osd.sink.audio) { osd.sink.audio.muted = !osd.sink.audio.muted; osd.show("volume", osd.sink.audio.volume, osd.sink.audio.muted) } }
    GlobalShortcut { appid: "nexus"; name: "micmute"
        onPressed: if (osd.source && osd.source.audio) { osd.source.audio.muted = !osd.source.audio.muted; osd.show("microphone", 1, osd.source.audio.muted) } }

    // Brightness: read, then set, with brightnessctl (built-in screens only).
    readonly property string brightnessctl: Quickshell.env("NEXUS_BRIGHTNESSCTL") || "brightnessctl"
    property bool brightnessUp: true
    Process {
        id: readBrightness
        command: [osd.brightnessctl, "-m", "-c", "backlight", "info"]
        stdout: StdioCollector {
            onStreamFinished: {
                const b = O.parseBrightness(text);
                if (!b) return;
                const v = O.brightnessStep(b.value, osd.brightnessUp);
                setBrightness.command = [osd.brightnessctl, "-d", b.device, "set", Math.round(v * 100) + "%"];
                setBrightness.running = true;
                osd.show("brightness", v, false);
            }
        }
    }
    Process { id: setBrightness }
    function brightness(up) { brightnessUp = up; if (!readBrightness.running) readBrightness.running = true }
    GlobalShortcut { appid: "nexus"; name: "brightup"; onPressed: osd.brightness(true) }
    GlobalShortcut { appid: "nexus"; name: "brightdown"; onPressed: osd.brightness(false) }

    // The keyboard's backlight, on laptops that have one (brightnessctl, the leds class).
    property string keyboardHow: "up"
    Process {
        id: readKeyboard
        command: [osd.brightnessctl, "-m", "-l", "-c", "leds"]
        stdout: StdioCollector {
            onStreamFinished: {
                const k = O.parseKeyboardLight(text);
                if (!k) return;
                const level = O.keyboardStep(k, osd.keyboardHow);
                setKeyboard.command = [osd.brightnessctl, "-d", k.device, "set", String(level)];
                setKeyboard.running = true;
                osd.show("keyboard", level / k.max, false);
            }
        }
    }
    Process { id: setKeyboard }
    function keyboardLight(how) { keyboardHow = how; if (!readKeyboard.running) readKeyboard.running = true }
    GlobalShortcut { appid: "nexus"; name: "kbdup"; onPressed: osd.keyboardLight("up") }
    GlobalShortcut { appid: "nexus"; name: "kbddown"; onPressed: osd.keyboardLight("down") }
    GlobalShortcut { appid: "nexus"; name: "kbdcycle"; onPressed: osd.keyboardLight("cycle") }

    // Caps Lock: bound on release without taking the key (desktop.nix), so
    // the state read here is the new one.
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    Process {
        id: readCaps
        command: [osd.hyprctl, "devices", "-j"]
        stdout: StdioCollector { onStreamFinished: { const on = O.capsLock(text); if (on !== null) osd.show("capslock", on ? 1 : 0, false) } }
    }
    GlobalShortcut { appid: "nexus"; name: "capslock"; onPressed: if (!readCaps.running) readCaps.running = true }

    PanelWindow {
        id: card
        visible: false
        anchors.bottom: true
        margins.bottom: 72
        implicitWidth: 260; implicitHeight: 56
        color: "transparent"
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.layer: WlrLayer.Overlay
        WlrLayershell.namespace: "nexus-osd"
        mask: Region {}
        Rectangle {
            anchors.fill: parent; radius: 18
            color: osd.theme ? osd.theme.glass : "#cc0c142a"
            border.color: osd.theme ? osd.theme.glassBorder : "#33ffffff"
            Image { x: 16; anchors.verticalCenter: parent.verticalCenter; width: 22; height: 22; sourceSize: Qt.size(44, 44)
                    source: "icons/" + O.icon(osd.kind) + ".svg"; opacity: osd.muted ? 0.4 : 1 }
            Rectangle {
                visible: O.hasBar(osd.kind)
                x: 50; width: parent.width - 120; height: 6; radius: 3; anchors.verticalCenter: parent.verticalCenter
                color: osd.theme ? osd.theme.chip : "#33ffffff"
                Rectangle { width: parent.width * (osd.muted ? 0 : Math.min(1, osd.value)); height: parent.height; radius: 3
                            color: osd.theme ? osd.theme.primary : "#9C8FFF" }
            }
            Text {
                anchors { right: parent.right; rightMargin: 16; verticalCenter: parent.verticalCenter }
                text: O.label(osd.kind, osd.value, osd.muted); color: osd.theme ? osd.theme.ink : "white"; font.pixelSize: 13
            }
            Accessible.role: Accessible.StatusBar
            Accessible.name: osd.kind + " " + O.label(osd.kind, osd.value, osd.muted)
        }
    }
}
