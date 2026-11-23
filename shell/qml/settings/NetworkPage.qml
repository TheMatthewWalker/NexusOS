import QtQuick
import Quickshell.Io
import "../lib/network.mjs" as Net
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
    Component.onCompleted: { wifi.scanning = true; refreshVpns() }
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
    // VPNs: NetworkManager's (nix/modules/everyday.nix adds OpenVPN, OpenConnect
    // for Cisco AnyConnect, GlobalProtect and Fortinet, vpnc and L2TP). uuids and
    // file types are checked before they reach nmcli.
    property var vpnList: []
    property string vpnMessage: ""
    function refreshVpns() { if (!vpnLister.running) vpnLister.running = true }
    Timer { interval: 5000; repeat: true; running: page.visible; onTriggered: page.refreshVpns() }
    Process { id: vpnLister; command: ["nmcli", "-t", "-f", "NAME,UUID,TYPE,ACTIVE", "connection", "show"]
              stdout: StdioCollector { onStreamFinished: page.vpnList = Net.vpns(text) } }
    Process {
        id: vpnSwitch
        stderr: StdioCollector { id: vpnErr }
        onExited: code => { page.vpnMessage = code === 0 ? "" : (vpnErr.text.trim().split("\n").pop() || "That did not work."); page.refreshVpns() }
    }
    function vpn(uuid, up) {
        if (!Net.validUuid(uuid) || vpnSwitch.running) return;
        vpnSwitch.command = ["nmcli", "connection", up ? "up" : "down", "uuid", uuid];
        vpnSwitch.running = true;
        vpnMessage = up ? "Connecting…" : "";
    }
    Process {
        id: vpnPicker
        command: ["zenity", "--file-selection", "--title=Choose a VPN file (.ovpn or WireGuard .conf)",
                  "--file-filter=VPN files | *.ovpn *.conf"]
        stdout: StdioCollector {
            onStreamFinished: {
                const path = text.trim(), kind = Net.importType(path);
                if (!path) return;
                if (!kind) { page.vpnMessage = "That is not a VPN file NexusOS knows (.ovpn or WireGuard .conf)."; return }
                vpnSwitch.command = ["nmcli", "connection", "import", "type", kind, "file", path];
                vpnSwitch.running = true;
                page.vpnMessage = "Adding the VPN…";
            }
        }
    }

    // Mobile hotspot: share this computer's internet over Wi-Fi (NetworkManager;
    // the person at the screen may, without a password). The hotspot's own
    // password is made here, shown to share, and kept by NetworkManager.
    property bool hotspotOn: false
    property string hotspotName: ""
    property string hotspotKey: ""
    property string hotspotNote: ""
    Process {
        id: hotspotState
        running: true
        command: ["sh", "-c", "nmcli -t -f NAME connection show --active | grep -qx Hotspot || exit 0; " +
                              "printf '%s\\n' \"$(nmcli -g 802-11-wireless.ssid connection show Hotspot)\" \"$(nmcli -s -g 802-11-wireless-security.psk connection show Hotspot)\""]
        stdout: StdioCollector { onStreamFinished: { const l = text.split("\n"); page.hotspotOn = l[0] !== ""; page.hotspotName = l[0] || ""; page.hotspotKey = l[1] || "" } }
    }
    Process {
        id: hotspotSwitch
        stdout: StdioCollector { id: hotspotOut }
        onExited: (code) => { page.hotspotNote = code === 0 ? "" : (hotspotOut.text.trim().split("\n").pop() || "The hotspot could not start: this Wi-Fi may not support it while connected."); hotspotState.running = true }
    }
    function hotspot(on) {
        if (hotspotSwitch.running) return;
        hotspotSwitch.command = on
            ? ["sh", "-c", "key=$(tr -dc 'abcdefghjkmnpqrstuvwxyz23456789' </dev/urandom | head -c 10); " +
                           "nmcli device wifi hotspot con-name Hotspot ssid \"NexusOS-$(hostname)\" password \"$key\" 2>&1"]
            : ["nmcli", "connection", "down", "Hotspot"];
        hotspotSwitch.running = true;
    }
    SettingRow {
        theme: page.theme; label: "Mobile hotspot"; highlighted: page.host.highlight === "hotspot"
        detail: page.hotspotNote || (page.hotspotOn
            ? "On. Connect a phone or laptop to " + page.hotspotName + " with the password " + page.hotspotKey + "."
            : "Share this computer's internet with a phone or another computer over Wi-Fi.")
        Button { theme: page.theme; label: page.hotspotOn ? "Turn off" : "Turn on"; enabled: !hotspotSwitch.running
                 onClicked: page.hotspot(!page.hotspotOn) }
    }

    SettingRow {
        theme: page.theme; label: "VPN"; highlighted: page.host.highlight === "vpn"
        detail: page.vpnMessage || (page.vpnList.length === 0 ? "None set up. Add one from the file your work, school or VPN provider gave you."
                                    : "Connect to reach a work or school network, or for privacy.")
        Button { theme: page.theme; label: "Add from a file…"; onClicked: if (!vpnPicker.running) vpnPicker.running = true }
    }
    Repeater {
        model: page.vpnList
        delegate: Rectangle {
            required property var modelData
            width: page.width; height: 48; radius: 10
            color: page.theme.chip
            border.color: modelData.active ? page.theme.focus : "transparent"
            Text { x: 16; anchors.verticalCenter: parent.verticalCenter; textFormat: Text.PlainText
                   text: modelData.name + " · " + modelData.kind + (modelData.active ? " · connected" : ""); color: page.theme.ink; font.pixelSize: 14 }
            Button { anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
                     theme: page.theme; label: modelData.active ? "Disconnect" : "Connect"
                     onClicked: page.vpn(modelData.uuid, !modelData.active) }
        }
    }
    SettingRow {
        theme: page.theme; label: "Work, school and company VPN connections"; highlighted: page.host.highlight === "advanced"
        detail: "Wi-Fi that needs a username or certificate (eduroam, company networks), and VPNs such as Cisco AnyConnect, GlobalProtect or Fortinet."
        Button { theme: page.theme; label: "Set up…"; onClicked: Quickshell.execDetached(["nm-connection-editor"]) }
    }

    // Shared folders on another computer or a NAS (Windows' \\server\share).
    SettingRow {
        theme: page.theme; label: "Shared folders"; highlighted: page.host.highlight === "shares"
        detail: "Type the address, as on Windows (\\\\server\\folder) or smb://server/folder. Files asks for the username and password if it needs them."
    }
    Row {
        spacing: 8
        TextField { id: shareAddress; theme: page.theme; label: "Address"; placeholder: "\\\\nas\\Photos"; width: 320
                    onAccepted: openShare.clicked() }
        Button {
            id: openShare
            anchors.bottom: parent.bottom
            theme: page.theme; label: "Open"
            onClicked: {
                const url = Net.smbUrl(shareAddress.text);
                if (!url) { page.vpnMessage = "That address does not look like a shared folder."; return }
                Quickshell.execDetached(["gio", "open", url]);
            }
        }
        Button { anchors.bottom: parent.bottom; theme: page.theme; label: "Browse the network"
                 onClicked: Quickshell.execDetached(["gio", "open", "network:///"]) }
    }
    // Sharing this computer's own folders (nexus-share): read-only to others.
    property bool sharingOn: false
    property var myShares: []
    property string shareNote: ""
    Process { id: sharingState; running: true; command: ["systemctl", "is-active", "--quiet", "samba-smbd.service"]
              onExited: (code) => { page.sharingOn = code === 0; if (page.sharingOn) shareLister.running = true } }
    Process { id: shareLister; command: ["nexus-share", "list"]
              stdout: StdioCollector { onStreamFinished: { try { const v = JSON.parse(text); page.myShares = Array.isArray(v) ? v : [] } catch (e) {} } } }
    Process {
        id: shareRunner
        stdout: StdioCollector { id: shareOut }
        onExited: { try { const v = JSON.parse(shareOut.text); page.shareNote = v.error || v.message || "" } catch (e) {} shareLister.running = true }
    }
    Process {
        id: sharePicker
        command: ["zenity", "--file-selection", "--directory", "--title=Choose a folder to share"]
        stdout: StdioCollector { onStreamFinished: { const p = text.trim(); if (p) { shareRunner.command = ["nexus-share", "add", p]; shareRunner.running = true } } }
    }
    SettingRow {
        theme: page.theme; label: "Share folders from this computer"; highlighted: page.host.highlight === "sharing"
        detail: page.shareNote || (page.sharingOn
            ? (page.myShares.length ? "Others on this network can open and copy these, not change them." : "Share a folder so other computers on this network can open it (read-only).")
            : "Off. Turning it on lets other computers on this network open folders you choose (read-only); it is a system change, checked by Guardian.")
        Button { visible: !page.sharingOn; theme: page.theme; label: "Turn on…"; onClicked: page.host.proposeSystem(Net.SHARING_PLAN) }
        Button { visible: page.sharingOn; theme: page.theme; label: "Share a folder…"; enabled: !sharePicker.running && !shareRunner.running
                 onClicked: sharePicker.running = true }
    }
    Repeater {
        model: page.myShares
        delegate: SettingRow {
            required property var modelData
            theme: page.theme; label: modelData.name; detail: modelData.path
            Button { theme: page.theme; label: "Stop sharing"; enabled: !shareRunner.running
                     onClicked: { shareRunner.command = ["nexus-share", "remove", modelData.name]; shareRunner.running = true } }
        }
    }
    SettingRow {
        theme: page.theme; label: "Your phone"; highlighted: page.host.highlight === "phone"
        detail: "KDE Connect links an Android phone (or an iPhone, with fewer features) on the same Wi-Fi: files both ways, notifications, texts and the clipboard. Install KDE Connect on the phone, then pair here."
        Button { theme: page.theme; label: "Open KDE Connect"; onClicked: Quickshell.execDetached(["kdeconnect-app"]) }
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
