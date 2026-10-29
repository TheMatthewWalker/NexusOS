import QtQuick
import Quickshell.Bluetooth

// Settings, Bluetooth: the adapter on or off, paired devices, and pairing a
// new one. Looking for devices runs only while this page is open.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property var adapter: Bluetooth.defaultAdapter
    readonly property bool on: !!(adapter && adapter.enabled)
    readonly property var devices: adapter && adapter.devices ? adapter.devices.values : []
    readonly property var paired: devices.filter(d => d.paired || d.bonded)
    readonly property var nearby: devices.filter(d => !(d.paired || d.bonded) && d.name)
    Binding { when: page.on; target: page.adapter; property: "discovering"; value: true }
    Component.onDestruction: if (page.on) page.adapter.discovering = false

    PageHeader { theme: page.theme; title: "Bluetooth"; summary: "Headphones, mice, keyboards and phones." }

    SettingRow {
        theme: page.theme; label: "Bluetooth"; highlighted: page.host.highlight === "bluetooth"
        detail: !page.adapter ? "No Bluetooth adapter was found." : !page.on ? "Off"
              : page.paired.filter(d => d.connected).length + " connected"
        Switch { theme: page.theme; name: "Bluetooth"; enabled: !!page.adapter; checked: page.on
                 onToggled: page.adapter.enabled = !page.adapter.enabled }
    }

    component DeviceRow: Rectangle {
        id: dev
        property var device
        width: page.width; height: 48; radius: 10
        color: page.theme.chip
        Column {
            x: 16; anchors.verticalCenter: parent.verticalCenter; width: parent.width - actions.width - 40
            Text { width: parent.width; text: dev.device.name || dev.device.address; textFormat: Text.PlainText
                   color: page.theme.ink; font.pixelSize: 14; elide: Text.ElideRight }
            Text {
                text: dev.device.pairing ? "Pairing…" : dev.device.connected
                      ? "Connected" + (dev.device.batteryAvailable ? " · battery " + Math.round(dev.device.battery * 100) + "%" : "")
                      : dev.device.paired ? "Paired" : "Nearby"
                color: page.theme.inkSecondary; font.pixelSize: 12
            }
        }
        Row {
            id: actions
            anchors { right: parent.right; rightMargin: 10; verticalCenter: parent.verticalCenter }
            spacing: 6
            Button { theme: page.theme; visible: dev.device.paired && !dev.device.connected; label: "Connect"
                     onClicked: dev.device.connect() }
            Button { theme: page.theme; visible: dev.device.connected; label: "Disconnect"; onClicked: dev.device.disconnect() }
            Button { theme: page.theme; visible: !dev.device.paired && !dev.device.pairing; primary: true; label: "Pair"
                     onClicked: { dev.device.trusted = true; dev.device.pair() } }
            Button { theme: page.theme; visible: dev.device.paired; danger: true; label: "Forget"; onClicked: dev.device.forget() }
        }
    }

    Text { visible: page.on && page.paired.length > 0; text: "Your devices"; color: page.theme.inkSecondary
           font.pixelSize: 12; font.weight: 600; topPadding: 6 }
    Repeater { model: page.on ? page.paired : []; delegate: DeviceRow { required property var modelData; device: modelData } }
    Text { visible: page.on; text: page.nearby.length ? "Nearby devices" : "Looking for nearby devices…"
           color: page.theme.inkSecondary; font.pixelSize: 12; font.weight: 600; topPadding: 6 }
    Repeater { model: page.on ? page.nearby.slice(0, 10) : []; delegate: DeviceRow { required property var modelData; device: modelData } }
}
