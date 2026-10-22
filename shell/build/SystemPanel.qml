import QtQuick
import Quickshell
import Quickshell.Wayland
import Quickshell.Services.Pipewire
import Quickshell.Services.UPower
import Quickshell.Networking
import Quickshell.Bluetooth
import "lib/tokens.mjs" as T

// The top-right orbital system panel (design pack: orbital-control.css). It is a
// small Nexus icon until hovered or clicked, then an orbit of system planets
// plus quick controls. Everything shown is read from the real services, and a
// service that is not there says so (binding rule 2: never fake status).
// Quick controls are runtime toggles through those services; persistent system
// configuration goes through Guardian, never from here (binding rule 3).
PanelWindow {
    id: win
    property var theme
    property bool reduced: false
    readonly property var oc: T.tokens.baseline.orbitalControl
    property bool expanded: false

    anchors { top: true; right: true }
    margins { top: expanded ? oc.expanded.top : oc.collapsed.top; right: expanded ? oc.expanded.right : oc.collapsed.right }
    implicitWidth: expanded ? oc.expanded.width : oc.collapsed.diameter
    implicitHeight: expanded ? Math.max(oc.expanded.height, oc.quickPanelTop + quickCard.height + 8) : oc.collapsed.diameter
    color: "transparent"
    exclusionMode: ExclusionMode.Ignore
    WlrLayershell.layer: WlrLayer.Top
    WlrLayershell.namespace: "nexus-system-panel"

    // ---- live state ----
    readonly property var sink: Pipewire.defaultAudioSink
    PwObjectTracker { objects: win.sink ? [win.sink] : [] }
    readonly property bool hasAudio: !!(sink && sink.audio)
    readonly property bool hasWifi: Networking.backend !== NetworkBackendType.None && Networking.wifiHardwareEnabled
    readonly property var adapter: Bluetooth.defaultAdapter
    readonly property var battery: UPower.displayDevice
    readonly property bool hasBattery: !!(battery && battery.isLaptopBattery && battery.isPresent)
    SystemClock { id: clock; precision: SystemClock.Minutes }

    readonly property var planets: [
        { id: "network", label: "Network", state: hasWifi ? (Networking.wifiEnabled ? "Wi-Fi on" : "Wi-Fi off") : "Unavailable" },
        { id: "power", label: "Power", state: hasBattery ? Math.round(battery.percentage * 100) + "%" + (UPower.onBattery ? "" : ", charging") : "Mains power" },
        { id: "bluetooth", label: "Bluetooth", state: adapter ? (adapter.enabled ? "On" : "Off") : "No adapter" },
        { id: "privacy", label: "Privacy", state: "Local only" },
        { id: "time", label: "Date & time", state: Qt.formatDateTime(clock.date, "ddd d MMM, HH:mm") },
        { id: "display", label: "Display", state: Quickshell.screens.length + (Quickshell.screens.length === 1 ? " screen" : " screens") },
        { id: "sound", label: "Sound", state: hasAudio ? (sink.audio.muted ? "Muted" : Math.round(sink.audio.volume * 100) + "%") : "No output" }
    ]
    property var focusPlanet: null

    Timer { id: leave; interval: win.oc.leaveDelay; onTriggered: win.expanded = false }
    HoverHandler {
        onHoveredChanged: hovered ? (leave.stop(), win.expanded = true) : leave.restart()
    }

    // Collapsed: a small Nexus mark.
    Rectangle {
        visible: !win.expanded
        anchors.fill: parent
        radius: width / 2
        color: win.theme.glass; border.color: win.theme.glassBorder
        Rectangle { anchors.centerIn: parent; width: 12; height: 12; radius: 6; color: "#9E8CFF"
                    border.color: "#D8D0FF"; border.width: 1 }
        TapHandler { onTapped: win.expanded = true }
        Accessible.role: Accessible.Button
        Accessible.name: "System controls"
    }

    // Expanded: the orbit of system planets and the quick controls.
    Item {
        visible: win.expanded
        anchors.fill: parent
        opacity: win.expanded ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: win.reduced ? 0 : win.oc.reveal } }

        Canvas {
            x: 0; y: win.oc.orbit.top; width: win.oc.orbit.width; height: win.oc.orbit.height
            onPaint: {
                const ctx = getContext("2d");
                ctx.strokeStyle = "rgba(140,160,255,0.35)";
                ctx.setLineDash([6, 5]);
                for (const k of [1, 0.66]) {
                    ctx.beginPath();
                    ctx.ellipse(width * (1 - k) / 2, height * (1 - k) / 2, width * k, height * k);
                    ctx.stroke();
                }
            }
        }
        Rectangle {
            x: win.oc.centreTopLeft.x; y: win.oc.centreTopLeft.y
            width: win.oc.centreDiameter; height: width; radius: width / 2
            color: win.theme.glass; border.color: win.theme.glassBorder
            Text { anchors.centerIn: parent; text: "Nexus"; color: win.theme.ink; font.pixelSize: 13; font.weight: 600 }
        }
        Repeater {
            model: win.planets
            delegate: Rectangle {
                required property var modelData
                readonly property var pos: win.oc.planetTopLeft[modelData.id]
                x: pos[0]; y: pos[1]
                width: win.oc.planetDiameter; height: width; radius: width / 2
                color: win.theme.glass
                border.color: hover.hovered ? win.theme.focus : win.theme.glassBorder
                scale: hover.hovered && !win.reduced ? win.oc.hoverScale : 1
                Behavior on scale { NumberAnimation { duration: win.oc.hoverTransition } }
                Image { anchors.centerIn: parent; width: 22; height: 22; sourceSize: Qt.size(44, 44); source: "icons/" + modelData.id + ".svg" }
                HoverHandler { id: hover; onHoveredChanged: win.focusPlanet = hovered ? modelData : null }
                Accessible.role: Accessible.StaticText
                Accessible.name: modelData.label + ": " + modelData.state
            }
        }
        // Description of the hovered planet, left of the orbit.
        Rectangle {
            visible: win.focusPlanet !== null
            x: 8; y: 0; width: desc.implicitWidth + 24; height: desc.implicitHeight + 16; radius: 13
            color: win.theme.glass; border.color: win.theme.glassBorder
            Column {
                id: desc
                anchors.centerIn: parent
                spacing: 2
                Text { text: win.focusPlanet ? win.focusPlanet.label : ""; color: win.theme.ink; font.pixelSize: 14; font.weight: 600 }
                Text { text: win.focusPlanet ? win.focusPlanet.state : ""; color: win.theme.inkSecondary; font.pixelSize: 13 }
            }
        }

        // Quick controls.
        Rectangle {
            id: quickCard
            x: 13; y: win.oc.quickPanelTop; width: parent.width - 26; height: quick.implicitHeight + 26
            radius: T.tokens.baseline.radii.orbitalQuickPanel
            color: win.theme.glass; border.color: win.theme.glassBorder
            Column {
                id: quick
                x: 15; y: 13; width: parent.width - 30
                spacing: 12
                Row {
                    spacing: 10; width: parent.width
                    Image { width: 20; height: 20; sourceSize: Qt.size(40, 40); source: "icons/sound.svg"; anchors.verticalCenter: parent.verticalCenter }
                    Item {
                        id: track
                        width: parent.width - 80; height: 24; anchors.verticalCenter: parent.verticalCenter
                        enabled: win.hasAudio
                        readonly property real value: win.hasAudio ? win.sink.audio.volume : 0
                        Rectangle { anchors.verticalCenter: parent.verticalCenter; width: parent.width; height: 4; radius: 2; color: win.theme.glassBorder }
                        Rectangle { anchors.verticalCenter: parent.verticalCenter; width: parent.width * Math.min(1, track.value); height: 4; radius: 2; color: win.theme.focus }
                        Rectangle { x: parent.width * Math.min(1, track.value) - 7; anchors.verticalCenter: parent.verticalCenter
                                    width: 14; height: 14; radius: 7; color: win.theme.ink; visible: win.hasAudio }
                        MouseArea {
                            anchors.fill: parent
                            function set(mx) { if (win.hasAudio) win.sink.audio.volume = Math.max(0, Math.min(1, mx / width)) }
                            onPressed: m => set(m.x)
                            onPositionChanged: m => { if (pressed) set(m.x) }
                        }
                        Accessible.role: Accessible.Slider
                        Accessible.name: "Volume"
                    }
                    Text {
                        width: 40; anchors.verticalCenter: parent.verticalCenter
                        text: win.hasAudio ? Math.round(track.value * 100) + "%" : "None"
                        color: win.theme.inkSecondary; font.pixelSize: 13
                    }
                }
                Row {
                    spacing: 8
                    Repeater {
                        model: [
                            { label: "Wi-Fi", on: win.hasWifi && Networking.wifiEnabled, available: win.hasWifi,
                              toggle: () => Networking.wifiEnabled = !Networking.wifiEnabled },
                            { label: "Bluetooth", on: !!(win.adapter && win.adapter.enabled), available: !!win.adapter,
                              toggle: () => win.adapter.enabled = !win.adapter.enabled },
                            { label: "Mute", on: win.hasAudio && win.sink.audio.muted, available: win.hasAudio,
                              toggle: () => win.sink.audio.muted = !win.sink.audio.muted }
                        ]
                        delegate: Rectangle {
                            required property var modelData
                            width: label.implicitWidth + 24; height: 32; radius: 10
                            color: modelData.on ? Qt.rgba(0.49, 0.42, 1.0, 0.28) : "transparent"
                            border.color: modelData.on ? win.theme.focus : win.theme.glassBorder
                            opacity: modelData.available ? 1 : 0.5
                            Text {
                                id: label; anchors.centerIn: parent
                                text: modelData.label + (modelData.available ? "" : " · n/a")
                                color: win.theme.ink; font.pixelSize: 13
                            }
                            TapHandler { enabled: modelData.available; onTapped: modelData.toggle() }
                            Accessible.role: Accessible.CheckBox
                            Accessible.checked: modelData.on
                            Accessible.name: modelData.label
                        }
                    }
                }
                Text {
                    width: parent.width; wrapMode: Text.Wrap
                    text: "System changes such as SSH or printing go through Guardian."
                    color: win.theme.inkSecondary; font.pixelSize: 13
                }
            }
        }
    }
}
