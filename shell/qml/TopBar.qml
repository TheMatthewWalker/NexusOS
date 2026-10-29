import QtQuick
import Quickshell
import Quickshell.Wayland
import Quickshell.Hyprland
import Quickshell.Services.Pipewire
import Quickshell.Services.UPower
import Quickshell.Networking

// The top bar (design: "Desktop B · Floating glass with top bar"): the Nexus
// orb, workspaces, the date and time, and Wi-Fi, sound and battery. Each
// status opens its page in Settings. Windows keep clear of it.
PanelWindow {
    id: bar
    property var theme
    property var settings
    signal openNexus()

    anchors { top: true; left: true; right: true }
    margins { top: 10; left: 12; right: 12 }
    implicitHeight: 40
    color: "transparent"
    WlrLayershell.layer: WlrLayer.Top
    WlrLayershell.namespace: "nexus-top-bar"

    SystemClock { id: clock; precision: SystemClock.Minutes }
    readonly property var sink: Pipewire.defaultAudioSink
    PwObjectTracker { objects: bar.sink ? [bar.sink] : [] }
    readonly property bool hasAudio: !!(sink && sink.audio)
    readonly property var battery: UPower.displayDevice
    readonly property bool hasBattery: !!(battery && battery.isLaptopBattery && battery.isPresent)
    readonly property var wifi: settings.wifi
    readonly property int activeWorkspace: Hyprland.focusedWorkspace ? Hyprland.focusedWorkspace.id : 1
    // Workspaces 1-5 always; a higher one shows while it has windows.
    readonly property var workspaces: {
        const ids = [1, 2, 3, 4, 5];
        for (const w of Hyprland.workspaces.values) if (w.id > 5 && !ids.includes(w.id)) ids.push(w.id);
        return ids.sort((a, b) => a - b);
    }
    function occupied(id) { return Hyprland.workspaces.values.some(w => w.id === id && w.toplevels.values.length > 0) }

    Rectangle {
        anchors.fill: parent
        radius: 14
        color: Qt.rgba(0.04, 0.07, 0.15, 0.86)
        border.color: bar.theme.glassBorder

        Row {
            x: 10; anchors.verticalCenter: parent.verticalCenter
            spacing: 10
            // The Nexus orb.
            Rectangle {
                width: 24; height: 24; radius: 12; anchors.verticalCenter: parent.verticalCenter
                gradient: Gradient {
                    GradientStop { position: 0; color: "#FFE3B8" }
                    GradientStop { position: 0.4; color: "#FFA65C" }
                    GradientStop { position: 1; color: "#8C50C8" }
                }
                border.color: orbHover.hovered ? bar.theme.focus : "transparent"
                HoverHandler { id: orbHover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: bar.openNexus() }
                Accessible.role: Accessible.Button
                Accessible.name: "Open Nexus"
            }
            Row {
                spacing: 4; anchors.verticalCenter: parent.verticalCenter
                Repeater {
                    model: bar.workspaces
                    delegate: Rectangle {
                        required property int modelData
                        readonly property bool active: modelData === bar.activeWorkspace
                        width: Math.max(28, wsLabel.implicitWidth + 16); height: 26; radius: 8
                        color: active ? bar.theme.primary : wsHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.2) : "transparent"
                        Text {
                            id: wsLabel; anchors.centerIn: parent
                            text: modelData
                            color: active ? "#FFFFFF" : bar.occupied(modelData) ? bar.theme.ink : bar.theme.inkSecondary
                            font.pixelSize: 13; font.weight: active ? 600 : 400
                        }
                        // A dot under a workspace that has windows.
                        Rectangle { visible: !active && bar.occupied(modelData); width: 4; height: 4; radius: 2
                                    anchors { horizontalCenter: parent.horizontalCenter; bottom: parent.bottom; bottomMargin: 2 }
                                    color: bar.theme.focus }
                        HoverHandler { id: wsHover; cursorShape: Qt.PointingHandCursor }
                        TapHandler { onTapped: Hyprland.dispatch("workspace " + modelData) }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Workspace " + modelData + (active ? ", current" : "")
                    }
                }
            }
        }

        Text {
            anchors.centerIn: parent
            text: Qt.formatDateTime(clock.date, "ddd d MMM · HH:mm")
            color: bar.theme.ink; font.pixelSize: 13; font.weight: 500
            TapHandler { onTapped: bar.settings.open("about", "") }
        }

        Row {
            anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
            spacing: 14
            component Status: Row {
                id: st
                property string icon
                property string label: ""
                property string name
                property string page
                property real dim: 1
                spacing: 6
                Image { width: 16; height: 16; sourceSize: Qt.size(32, 32); source: "icons/" + st.icon + ".svg"
                        opacity: st.dim; anchors.verticalCenter: parent.verticalCenter }
                Text { visible: st.label !== ""; text: st.label; color: bar.theme.inkSecondary; font.pixelSize: 12
                       anchors.verticalCenter: parent.verticalCenter }
                HoverHandler { cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: bar.settings.open(st.page, "") }
                Accessible.role: Accessible.Button
                Accessible.name: st.name
            }
            Status {
                icon: "network"; page: "network"; dim: bar.wifi.ssid || bar.wifi.wired ? 1 : 0.4
                name: bar.wifi.ssid ? "Wi-Fi: " + bar.wifi.ssid : bar.wifi.wired ? "Wired" : "Not connected"
            }
            Status {
                icon: "sound"; page: "sound"; dim: bar.hasAudio && !bar.sink.audio.muted ? 1 : 0.4
                label: bar.hasAudio ? (bar.sink.audio.muted ? "Muted" : Math.round(bar.sink.audio.volume * 100) + "%") : ""
                name: "Sound: " + (bar.hasAudio ? label : "no output device")
            }
            Status {
                visible: bar.hasBattery
                icon: "battery"; page: "power"
                label: bar.hasBattery ? Math.round(bar.battery.percentage * 100) + "%" : ""
                name: "Battery " + label
            }
            Status { icon: "settings"; page: ""; name: "Settings" }
        }
    }
}
