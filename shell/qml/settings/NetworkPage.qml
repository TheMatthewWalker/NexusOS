import QtQuick
import Quickshell
import "../lib/wifi.mjs" as W

// Settings, Network: Wi-Fi on or off, the nearby networks, and the wired
// connection. Click a network to join it; a secured one asks for its
// password, which goes straight to NetworkManager.
Column {
    id: page
    property var theme
    property var host
    readonly property var wifi: host.wifi
    width: parent ? parent.width : 600
    spacing: 10
    Component.onCompleted: wifi.scanning = true
    Component.onDestruction: wifi.scanning = false

    PageHeader { theme: page.theme; title: "Network"
                 summary: "Wi-Fi and wired connections. Passwords are kept by NetworkManager, never by the desktop." }

    SettingRow {
        theme: page.theme; label: "Wi-Fi"; highlighted: page.host.highlight === "wifi"
        detail: !page.wifi.hasWifi ? "No Wi-Fi adapter was found, or it is switched off with a hardware key."
              : !page.wifi.on ? "Off" : page.wifi.ssid ? "Connected to " + page.wifi.ssid : "On, not connected"
        Switch { theme: page.theme; name: "Wi-Fi"; enabled: page.wifi.hasWifi; checked: page.wifi.on
                 onToggled: page.wifi.setEnabled(!page.wifi.on) }
    }

    // The network list.
    Rectangle {
        visible: page.wifi.on
        width: parent.width; height: list.implicitHeight + 20
        radius: 12; color: page.theme.chip
        Column {
            id: list
            x: 10; y: 10; width: parent.width - 20
            spacing: 4
            Text {
                visible: page.wifi.rows.length === 0
                leftPadding: 6; topPadding: 6; bottomPadding: 6
                text: "Looking for networks…"; color: page.theme.inkSecondary; font.pixelSize: 13
            }
            Repeater {
                id: netRepeater
                // ScriptModel keeps existing rows as the list changes (signal
                // strength, order) instead of rebuilding them all; it needs unique
                // values, so the model holds the network objects themselves.
                model: ScriptModel { values: page.wifi.rows.map(r => r.network) }
                delegate: Column {
                    id: netRow
                    required property var modelData
                    readonly property var row: page.wifi.rows.find(r => r.network === modelData)
                        || ({ name: "", signal: NaN, security: "unsupported", connected: false, known: false, state: "" })
                    readonly property Item rowItem: rowRect
                    readonly property Item forgetItem: forgetButton
                    width: parent.width; spacing: 4
                    Rectangle {
                        id: rowRect
                        width: parent.width; height: 40; radius: 9
                        color: netHover.hovered || netRow.row.connected ? Qt.rgba(0.49, 0.42, 1.0, 0.18) : "transparent"
                        border.color: netRow.row.connected ? page.theme.focus : "transparent"
                        Image { x: 10; anchors.verticalCenter: parent.verticalCenter; width: 18; height: 18
                                sourceSize: Qt.size(36, 36); source: "../icons/network.svg"
                                opacity: 0.35 + 0.65 * Math.max(0, Math.min(1, netRow.row.signal || 0)) }
                        Text {
                            x: 38; anchors.verticalCenter: parent.verticalCenter
                            width: parent.width - status.width - 60; elide: Text.ElideRight
                            text: netRow.row.name; textFormat: Text.PlainText
                            color: page.theme.ink; font.pixelSize: 14
                        }
                        Text {
                            id: status
                            anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
                            text: [W.stateLabel(netRow.row), W.signalLabel(netRow.row.signal),
                                   W.securityLabel(netRow.row.security)].filter(x => x).join(" · ")
                            color: page.theme.inkSecondary; font.pixelSize: 12
                        }
                        HoverHandler { id: netHover; cursorShape: Qt.PointingHandCursor }
                        TapHandler { enabled: netRow.row.name !== ""; onTapped: page.wifi.click(netRow.row) }
                        Accessible.role: Accessible.Button
                        Accessible.name: netRow.row.name + ", " + status.text
                        Accessible.onPressAction: page.wifi.click(netRow.row)
                    }
                    Row {
                        visible: netRow.row.connected && page.wifi.selected === netRow.row.name
                        spacing: 8; leftPadding: 38; bottomPadding: 6
                        Button { theme: page.theme; label: "Disconnect"; onClicked: page.wifi.disconnect(netRow.row) }
                        Button { id: forgetButton; theme: page.theme; danger: true; label: "Forget"
                                 onClicked: page.wifi.forget(netRow.row) }
                    }
                }
            }
        }
    }

    // The password box, outside the list: the list is rebuilt as signal strengths
    // change, which must not lose what is being typed. The text goes straight to
    // NetworkManager and is cleared.
    Rectangle {
        visible: page.wifi.passwordFor !== ""
        readonly property string network: page.wifi.passwordFor
        onNetworkChanged: password.text = ""
        onVisibleChanged: if (visible) password.forceActiveFocus(); else password.text = ""
        width: parent.width; height: pwColumn.implicitHeight + 24
        radius: 12; color: page.theme.chip; border.color: page.theme.focus
        Column {
            id: pwColumn
            x: 16; y: 12; width: parent.width - 32; spacing: 10
            Text { text: "Password for " + page.wifi.passwordFor; textFormat: Text.PlainText
                   color: page.theme.ink; font.pixelSize: 14; font.weight: 500 }
            Rectangle {
                width: parent.width; height: 38; radius: 9
                color: "transparent"; border.color: password.activeFocus ? page.theme.focus : page.theme.glassBorder
                TextInput {
                    id: password
                    x: 12; width: parent.width - 24; anchors.verticalCenter: parent.verticalCenter
                    echoMode: reveal.checked ? TextInput.Normal : TextInput.Password
                    color: page.theme.ink; font.pixelSize: 14; clip: true
                    inputMethodHints: Qt.ImhSensitiveData | Qt.ImhNoPredictiveText
                    onAccepted: if (page.wifi.submitPassword(text)) text = ""
                    Keys.onEscapePressed: page.wifi.passwordFor = ""
                    Accessible.role: Accessible.EditableText
                    Accessible.passwordEdit: !reveal.checked
                    Accessible.name: "Password for " + page.wifi.passwordFor
                }
            }
            Row {
                spacing: 8
                Button { theme: page.theme; primary: true; label: "Connect"
                         onClicked: if (page.wifi.submitPassword(password.text)) password.text = "" }
                Button { id: reveal; theme: page.theme; property bool checked: false
                         label: checked ? "Hide" : "Show"; onClicked: checked = !checked }
                Button { theme: page.theme; label: "Cancel"; onClicked: { page.wifi.passwordFor = ""; page.wifi.error = "" } }
            }
        }
    }
    Text {
        visible: page.wifi.error !== "" || page.wifi.note !== ""
        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.wifi.error || page.wifi.note
        color: page.wifi.error ? page.theme.error : page.theme.inkSecondary; font.pixelSize: 13
    }

    SettingRow {
        theme: page.theme; label: "Wired"; highlighted: page.host.highlight === "wired"
        detail: page.wifi.wired ? "Connected with a cable." : "No cable connected."
    }
    SettingRow {
        theme: page.theme; label: "Work, school and VPN connections"; highlighted: page.host.highlight === "advanced"
        detail: "Networks that need a username or certificate, and VPNs, are set up in a terminal with nmtui."
        Button { theme: page.theme; label: "Open nmtui"; onClicked: page.host.runInTerminal(["nmtui"]) }
    }

    // For `nexus-shell-ipc call settings wifi`: each row's centre, in window coordinates.
    function ipcRows() {
        const rows = [];
        for (let i = 0; i < netRepeater.count; i++) {
            const item = netRepeater.itemAt(i);
            if (!item || !item.row.name) continue;
            const at = it => { const p = it.mapToItem(null, it.width / 2, it.height / 2); return [Math.round(p.x), Math.round(p.y)] };
            const [x, y] = at(item.rowItem);
            rows.push({ name: item.row.name, connected: item.row.connected, known: item.row.known, state: item.row.state,
                        security: item.row.security, x: x, y: y, forget: item.forgetItem.visible ? at(item.forgetItem) : null });
        }
        return rows;
    }
}
