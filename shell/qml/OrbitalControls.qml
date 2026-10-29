import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Services.Pipewire
import Quickshell.Services.UPower
import Quickshell.Networking
import Quickshell.Bluetooth
import "lib/tokens.mjs" as T
import "lib/describe.mjs" as D

// The orbital controls (design pack: orbital-control.css), in the bottom-right
// corner, which nothing else uses. A small Nexus mark until hovered, then an
// orbit of system planets over a nebula. They are shortcuts: hovering a planet
// says in one plain sentence how that part of the system is (feature 3, built
// by lib/describe.mjs from the real readings), and clicking it opens its page
// in Settings, where the controls are. A service that is not there says so
// (binding rule 2: never fake status).
PanelWindow {
    id: win
    property var theme
    property var settings
    property bool reduced: false
    readonly property var oc: T.tokens.baseline.orbitalControl
    property bool expanded: false
    readonly property int descTop: oc.orbit.top + oc.orbit.height + 10

    anchors { bottom: true; right: true }
    margins { bottom: expanded ? 12 : 16; right: expanded ? oc.expanded.right : oc.collapsed.right }
    implicitWidth: expanded ? oc.expanded.width : oc.collapsed.diameter
    implicitHeight: expanded ? descTop + 96 : oc.collapsed.diameter
    color: "transparent"
    exclusionMode: ExclusionMode.Ignore
    WlrLayershell.layer: WlrLayer.Top
    WlrLayershell.namespace: "nexus-orbital-controls"

    // ---- readings for the sentences ----
    readonly property var sink: Pipewire.defaultAudioSink
    PwObjectTracker { objects: win.sink ? [win.sink] : [] }
    readonly property bool hasAudio: !!(sink && sink.audio)
    readonly property bool hasWifi: Networking.backend !== NetworkBackendType.None && Networking.wifiHardwareEnabled
    readonly property var adapter: Bluetooth.defaultAdapter
    readonly property var battery: UPower.displayDevice
    readonly property bool hasBattery: !!(battery && battery.isLaptopBattery && battery.isPresent)
    readonly property var wifi: settings.wifi
    SystemClock { id: clock; precision: SystemClock.Minutes }
    readonly property var btConnected: adapter && adapter.devices ? adapter.devices.values.filter(d => d.connected).length : null
    readonly property string batteryState: !hasBattery ? "unknown" : ({
        [UPowerDeviceState.Charging]: "charging", [UPowerDeviceState.Discharging]: "discharging",
        [UPowerDeviceState.FullyCharged]: "full", [UPowerDeviceState.Empty]: "empty",
        [UPowerDeviceState.PendingCharge]: "pending-charge", [UPowerDeviceState.PendingDischarge]: "pending-discharge"
    })[battery.state] || "unknown"

    // Each planet and the Settings page it opens.
    readonly property var planets: [
        { id: "network", label: "Network", page: "network",
          sentence: D.network({ available: Networking.backend !== NetworkBackendType.None, wifiHardware: hasWifi,
                                wifiEnabled: Networking.wifiEnabled, ssid: wifi.ssid, wired: wifi.wired, connectivity: wifi.connectivity }) },
        { id: "power", label: "Power", page: "power",
          sentence: D.power({ available: !!(battery && battery.ready), present: hasBattery, state: batteryState,
                              percentage: hasBattery ? battery.percentage : NaN,
                              timeToEmpty: hasBattery ? battery.timeToEmpty : 0, timeToFull: hasBattery ? battery.timeToFull : 0 }) },
        { id: "bluetooth", label: "Bluetooth", page: "bluetooth",
          sentence: D.bluetooth({ available: !!adapter, enabled: !!(adapter && adapter.enabled), connected: btConnected }) },
        { id: "privacy", label: "Privacy and AI", page: "privacy", sentence: "Nexus runs on this computer." },
        { id: "time", label: "Date & time", page: "about", sentence: D.time(clock.date, Quickshell.env("TZ") || "") },
        { id: "display", label: "Display", page: "display", sentence: D.display(Quickshell.screens.length) },
        { id: "sound", label: "Sound", page: "sound",
          sentence: D.sound({ available: hasAudio, muted: hasAudio && sink.audio.muted, volume: hasAudio ? sink.audio.volume : NaN,
                              device: sink ? (sink.description || sink.nickname || "") : "" }) }
    ]
    property string focusId: ""
    readonly property var focusPlanet: planets.find(p => p.id === focusId) || null
    function openPage(page) { expanded = false; settings.open(page, "") }
    // For tests and previews: `nexus-shell-ipc call controls expand true`, `... hover sound`.
    IpcHandler {
        target: "controls"
        function expand(on: bool): void { win.expanded = on }
        function hover(id: string): void { win.expanded = true; win.focusId = id }
        function state(): string { return JSON.stringify({ expanded: win.expanded, focus: win.focusId }) }
    }

    onExpandedChanged: if (!expanded) focusId = ""
    // A little longer than the design's leave delay: the panel grows from the
    // small mark as it opens, and the pointer must not count as leaving meanwhile.
    Timer { id: leave; interval: Math.max(600, win.oc.leaveDelay); onTriggered: win.expanded = false }
    HoverHandler { onHoveredChanged: hovered ? (leave.stop(), win.expanded = true) : leave.restart() }

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

    Item {
        visible: win.expanded
        anchors.fill: parent
        opacity: win.expanded ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: win.reduced ? 0 : win.oc.reveal } }

        Nebula { anchors.fill: parent; seed: 23 }
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
        // The centre opens Settings itself.
        Rectangle {
            x: win.oc.centreTopLeft.x; y: win.oc.centreTopLeft.y
            width: win.oc.centreDiameter; height: width; radius: width / 2
            color: win.theme.glass; border.color: centreHover.hovered ? win.theme.focus : win.theme.glassBorder
            Column {
                anchors.centerIn: parent; spacing: 2
                Image { anchors.horizontalCenter: parent.horizontalCenter; width: 22; height: 22; sourceSize: Qt.size(44, 44)
                        source: "icons/settings.svg" }
                Text { text: "Settings"; color: win.theme.ink; font.pixelSize: 12; font.weight: 600 }
            }
            HoverHandler { id: centreHover; cursorShape: Qt.PointingHandCursor
                           onHoveredChanged: if (hovered) win.focusId = "" }
            TapHandler { onTapped: win.openPage("") }
            Accessible.role: Accessible.Button
            Accessible.name: "Open Settings"
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
                HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor; onHoveredChanged: if (hovered) win.focusId = modelData.id }
                TapHandler { onTapped: win.openPage(modelData.page) }
                Accessible.role: Accessible.Button
                Accessible.name: modelData.label + ": " + modelData.sentence + ". Opens " + modelData.label + " settings."
            }
        }
        // The hovered planet, in words, under the orbit.
        Rectangle {
            x: 12; y: win.descTop; width: parent.width - 24; height: desc.implicitHeight + 18; radius: 13
            color: win.theme.glass; border.color: win.theme.glassBorder
            Column {
                id: desc
                x: 14; y: 9; width: parent.width - 28
                spacing: 3
                Text { text: win.focusPlanet ? win.focusPlanet.label : "System"; color: win.theme.ink; font.pixelSize: 14; font.weight: 600 }
                Text {
                    width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                    text: win.focusPlanet ? win.focusPlanet.sentence : "Hover a planet to see how it is; click it to change it."
                    color: win.theme.inkSecondary; font.pixelSize: 13
                }
                Text { visible: !!win.focusPlanet; text: "Click to open its settings"; color: win.theme.focus; font.pixelSize: 12 }
            }
        }
    }
}
