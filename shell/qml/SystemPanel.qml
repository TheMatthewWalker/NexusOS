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
import "lib/wifi.mjs" as W

// The top-right orbital system panel (design pack: orbital-control.css). It is a
// small Nexus icon until hovered or clicked, then an orbit of system planets
// plus quick controls. Everything shown is read from the real services, and a
// service that is not there says so (binding rule 2: never fake status).
// Quick controls are runtime toggles through those services; persistent system
// configuration goes through Guardian, never from here (binding rule 3).
// Hovering a planet shows one plain sentence (feature 3), built by
// lib/describe.mjs from the same readings; an unknown reading is left out.
// With Wi-Fi on, the quick controls list nearby networks (lib/wifi.mjs);
// connecting goes to NetworkManager, which keeps the password, never the shell.
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
    // While open, a click gives the panel the keyboard (for a Wi-Fi password).
    WlrLayershell.keyboardFocus: expanded ? WlrKeyboardFocus.OnDemand : WlrKeyboardFocus.None

    // ---- live state ----
    readonly property var sink: Pipewire.defaultAudioSink
    PwObjectTracker { objects: win.sink ? [win.sink] : [] }
    readonly property bool hasAudio: !!(sink && sink.audio)
    readonly property bool hasWifi: Networking.backend !== NetworkBackendType.None && Networking.wifiHardwareEnabled
    readonly property var adapter: Bluetooth.defaultAdapter
    readonly property var battery: UPower.displayDevice
    readonly property bool hasBattery: !!(battery && battery.isLaptopBattery && battery.isPresent)
    SystemClock { id: clock; precision: SystemClock.Minutes }

    // Readings the sentences need beyond the quick-control state above.
    readonly property var netDevices: Networking.devices ? Networking.devices.values : []
    readonly property string ssid: {
        for (const d of netDevices)
            if (d.type === DeviceType.Wifi && d.networks)
                for (const n of d.networks.values) if (n.connected) return n.name;
        return "";
    }
    readonly property bool wired: netDevices.some(d => d.type === DeviceType.Wired && d.connected)

    // ---- Wi-Fi networks ----
    // The Wi-Fi adapter NetworkManager manages. A machine can have others it
    // leaves alone (one running as an access point, say), which have no list.
    readonly property var wifiDevice: netDevices.find(d => d.type === DeviceType.Wifi && d.nmManaged)
        || netDevices.find(d => d.type === DeviceType.Wifi) || null
    readonly property bool wifiOn: hasWifi && Networking.wifiEnabled
    // Scan only while the panel is open, so a closed panel costs no radio time.
    Binding { when: win.wifiDevice !== null; target: win.wifiDevice; property: "scannerEnabled"; value: win.expanded && win.wifiOn }
    readonly property var securityNames: ({
        [WifiSecurityType.Open]: "Open", [WifiSecurityType.Owe]: "Owe", [WifiSecurityType.WpaPsk]: "WpaPsk",
        [WifiSecurityType.Wpa2Psk]: "Wpa2Psk", [WifiSecurityType.Sae]: "Sae", [WifiSecurityType.WpaEap]: "WpaEap",
        [WifiSecurityType.Wpa2Eap]: "Wpa2Eap", [WifiSecurityType.Wpa3SuiteB192]: "Wpa3SuiteB192",
        [WifiSecurityType.Leap]: "Leap", [WifiSecurityType.DynamicWep]: "DynamicWep", [WifiSecurityType.StaticWep]: "StaticWep"
    })
    readonly property var stateNames: ({ [ConnectionState.Connecting]: "Connecting", [ConnectionState.Connected]: "Connected",
                                         [ConnectionState.Disconnecting]: "Disconnecting", [ConnectionState.Disconnected]: "Disconnected" })
    readonly property var failNames: ({
        [ConnectionFailReason.NoSecrets]: "NoSecrets", [ConnectionFailReason.WifiClientDisconnected]: "WifiClientDisconnected",
        [ConnectionFailReason.WifiClientFailed]: "WifiClientFailed", [ConnectionFailReason.WifiAuthTimeout]: "WifiAuthTimeout",
        [ConnectionFailReason.WifiNetworkLost]: "WifiNetworkLost"
    })
    readonly property var wifiRows: !wifiOn || !wifiDevice || !wifiDevice.networks ? [] : W.rows(wifiDevice.networks.values.map(n => ({
        name: n.name, signal: n.signalStrength, securityName: securityNames[n.security] || "Unknown",
        security: W.securityKind(securityNames[n.security]), connected: n.connected, known: n.known,
        state: stateNames[n.state] || "Unknown", network: n })))
    property string selectedNetwork: ""     // a connected network whose Disconnect and Forget are showing
    property string passwordFor: ""         // the network whose password box is open
    property string passwordSecurity: ""
    property string wifiError: ""
    property string wifiNote: ""
    function wifiClick(row) {
        wifiError = ""; wifiNote = "";
        switch (W.clickAction(row)) {
        case "details": selectedNetwork = selectedNetwork === row.name ? "" : row.name; break;
        case "connect": passwordFor = ""; row.network.connect(); break;
        case "password": passwordFor = row.name; passwordSecurity = row.securityName; break;
        default: wifiNote = W.explanation(row);
        }
    }
    function submitPassword(text) {
        const problem = W.passwordProblem(text, passwordSecurity);
        if (problem) { wifiError = problem; return false; }
        const row = wifiRows.find(r => r.name === passwordFor);
        if (row) row.network.connectWithPsk(text);
        passwordFor = ""; wifiError = "";
        return true;
    }
    // Read-only state for tests and scripts: `nexus-shell-ipc call systempanel wifi`.
    // Each row's x and y are its centre on screen, so a test can click it with real input.
    IpcHandler {
        target: "systempanel"
        function wifi(): string {
            const rows = [];
            for (let i = 0; i < netRepeater.count; i++) {
                const item = netRepeater.itemAt(i);
                if (!item) continue;
                const at = it => {
                    const p = it.mapToItem(null, it.width / 2, it.height / 2);
                    return [Math.round(win.screenRight - win.width + p.x), Math.round(win.margins.top + p.y)];
                };
                const row = win.wifiRows[i];
                const [x, y] = at(item.rowItem);
                rows.push({ name: row.name, connected: row.connected, known: row.known, state: row.state, security: row.security,
                            x: x, y: y, forget: item.forgetItem.visible ? at(item.forgetItem) : null });
            }
            return JSON.stringify({ expanded: win.expanded, wifiOn: win.wifiOn, passwordFor: win.passwordFor,
                                    error: win.wifiError, note: win.wifiNote, rows: rows });
        }
    }
    readonly property real screenRight: (win.screen ? win.screen.width : 0) - win.margins.right
    // Failures are watched on Quickshell's own network list, whose objects
    // outlive the rebuilt rows above.
    Instantiator {
        model: win.wifiDevice ? win.wifiDevice.networks : null
        delegate: Connections {
            required property var modelData
            target: modelData
            function onConnectionFailed(reason) {
                win.wifiFailed({ name: modelData.name, securityName: win.securityNames[modelData.security] || "Unknown",
                                 security: W.securityKind(win.securityNames[modelData.security]) }, reason)
            }
        }
    }
    function wifiFailed(row, reason) {
        const name = failNames[reason] || "Unknown";
        // A saved network whose password changed, or a first try that was wrong: ask (again).
        if (row.security === "password" && name !== "WifiNetworkLost") { passwordFor = row.name; passwordSecurity = row.securityName; }
        wifiError = row.name + ": " + W.failMessage(name);
    }
    readonly property string connectivity: Networking.canCheckConnectivity && Networking.connectivityCheckEnabled
        ? ({ [NetworkConnectivity.Full]: "full", [NetworkConnectivity.Limited]: "limited",
             [NetworkConnectivity.Portal]: "portal", [NetworkConnectivity.None]: "none" })[Networking.connectivity] || "unknown"
        : "unknown"
    readonly property var btConnected: adapter && adapter.devices ? adapter.devices.values.filter(d => d.connected).length : null
    readonly property string batteryState: !hasBattery ? "unknown" : ({
        [UPowerDeviceState.Charging]: "charging", [UPowerDeviceState.Discharging]: "discharging",
        [UPowerDeviceState.FullyCharged]: "full", [UPowerDeviceState.Empty]: "empty",
        [UPowerDeviceState.PendingCharge]: "pending-charge", [UPowerDeviceState.PendingDischarge]: "pending-discharge"
    })[battery.state] || "unknown"

    // Disk usage has no Quickshell service: read it from df once a minute.
    property var volumes: []
    readonly property bool lowDisk: D.diskLow(volumes)
    Process {
        id: df
        command: ["df", "-P", "-B1", "/", "/home"]
        // df exits 1 if /home is missing but still reports /, so parse whatever it printed.
        stdout: StdioCollector { onStreamFinished: win.volumes = D.parseDf(text) }
    }
    Timer { interval: 60000; running: true; repeat: true; triggeredOnStart: true
            onTriggered: if (!df.running) df.running = true }

    readonly property var planets: [
        { id: "network", label: "Network",
          sentence: D.network({ available: Networking.backend !== NetworkBackendType.None, wifiHardware: hasWifi,
                                wifiEnabled: Networking.wifiEnabled, ssid: ssid, wired: wired, connectivity: connectivity }) },
        { id: "power", label: "Power",
          sentence: D.power({ available: !!(battery && battery.ready), present: hasBattery, state: batteryState,
                              percentage: hasBattery ? battery.percentage : NaN,
                              timeToEmpty: hasBattery ? battery.timeToEmpty : 0, timeToFull: hasBattery ? battery.timeToFull : 0 }) },
        { id: "bluetooth", label: "Bluetooth",
          sentence: D.bluetooth({ available: !!adapter, enabled: !!(adapter && adapter.enabled), connected: btConnected }) },
        { id: "privacy", label: "Privacy", sentence: "Local only" },
        { id: "time", label: "Date & time", sentence: D.time(clock.date, Quickshell.env("TZ") || "") },
        { id: "display", label: "Display", sentence: D.display(Quickshell.screens.length) },
        { id: "sound", label: "Sound", action: "Sound settings",
          sentence: D.sound({ available: hasAudio, muted: hasAudio && sink.audio.muted, volume: hasAudio ? sink.audio.volume : NaN,
                              device: sink ? (sink.description || sink.nickname || "") : "" }) }
    ]
    // The hovered planet. It lingers briefly so the pointer can reach its action button.
    property string focusId: ""
    readonly property var focusPlanet: planets.find(p => p.id === focusId) || null
    Timer { id: unfocus; interval: 400; onTriggered: win.focusId = "" }

    // "Sound settings": the sound controls are this panel's own quick controls, so point to them.
    property bool highlightControls: false
    Timer { id: highlight; interval: 2000; onTriggered: win.highlightControls = false }
    function planetAction(id) {
        if (id === "sound") { highlightControls = true; highlight.restart(); }
    }

    // "Show biggest files": the files role's app (AppDirectory), opened in the home folder.
    AppDirectory { id: apps }
    function openHomeFiles() {
        const entry = apps.entryFor("files");
        if (!entry) return;
        const home = Quickshell.env("HOME") || "";
        const command = Array.from(entry.command || []).filter(a => !/^%[a-zA-Z]$/.test(a));
        if (command.length === 0 || !home) { apps.open("files"); return; }
        Quickshell.execDetached({ command: command.concat([home]), workingDirectory: home });
    }

    // A small text button for suggested actions.
    component TextButton: Rectangle {
        id: btn
        property string label
        property var theme       // inline components do not see the outer ids, so pass the theme in
        signal clicked()
        width: btnText.implicitWidth + 20; height: 28; radius: 9
        color: btnHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.28) : "transparent"
        border.color: btn.theme.focus
        Text { id: btnText; anchors.centerIn: parent; text: btn.label; color: btn.theme.ink; font.pixelSize: 13 }
        HoverHandler { id: btnHover }
        TapHandler { onTapped: btn.clicked() }
        Accessible.role: Accessible.Button
        Accessible.name: btn.label
        Accessible.onPressAction: btn.clicked()
    }

    onExpandedChanged: if (!expanded) focusId = ""
    // Stay open while a password is being typed, even if the pointer wanders off.
    Timer { id: leave; interval: win.oc.leaveDelay; onTriggered: if (win.passwordFor === "") win.expanded = false }
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
                HoverHandler {
                    id: hover
                    onHoveredChanged: hovered ? (unfocus.stop(), win.focusId = modelData.id) : unfocus.restart()
                }
                Accessible.role: Accessible.StaticText
                Accessible.name: modelData.label + ": " + modelData.sentence
            }
        }
        // Description of the hovered planet, left of the orbit.
        Rectangle {
            visible: win.focusPlanet !== null
            x: 8; y: 0; width: 264; height: desc.implicitHeight + 16; radius: 13
            color: win.theme.glass; border.color: win.theme.glassBorder
            HoverHandler { onHoveredChanged: hovered ? unfocus.stop() : unfocus.restart() }
            Column {
                id: desc
                x: 12; y: 8; width: parent.width - 24
                spacing: 4
                Text { text: win.focusPlanet ? win.focusPlanet.label : ""; color: win.theme.ink; font.pixelSize: 14; font.weight: 600 }
                Text {
                    width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                    text: win.focusPlanet ? win.focusPlanet.sentence : ""
                    color: win.theme.inkSecondary; font.pixelSize: 13
                }
                TextButton {
                    theme: win.theme
                    visible: !!(win.focusPlanet && win.focusPlanet.action)
                    label: win.focusPlanet && win.focusPlanet.action ? win.focusPlanet.action : ""
                    onClicked: win.planetAction(win.focusId)
                }
            }
        }

        // Quick controls.
        Rectangle {
            id: quickCard
            x: 13; y: win.oc.quickPanelTop; width: parent.width - 26; height: quick.implicitHeight + 26
            radius: T.tokens.baseline.radii.orbitalQuickPanel
            color: win.theme.glass
            border.color: win.highlightControls ? win.theme.focus : win.theme.glassBorder
            border.width: win.highlightControls ? 2 : 1
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
                // Wi-Fi networks: click one to connect; a secured one asks for its password first.
                Column {
                    visible: win.wifiOn
                    width: parent.width; spacing: 4
                    Text { text: "Wi-Fi networks"; color: win.theme.ink; font.pixelSize: 13; font.weight: 600 }
                    Text {
                        visible: win.wifiRows.length === 0
                        text: "Looking for networks…"
                        color: win.theme.inkSecondary; font.pixelSize: 13
                    }
                    Repeater {
                        id: netRepeater
                        model: win.wifiRows
                        delegate: Column {
                            id: netRow
                            required property var modelData
                            width: parent.width; spacing: 4
                            readonly property Item rowItem: rowRect
                            readonly property Item forgetItem: forgetButton
                            Rectangle {
                                id: rowRect
                                width: parent.width; height: 34; radius: 9
                                color: netHover.hovered || netRow.modelData.connected ? Qt.rgba(0.49, 0.42, 1.0, 0.18) : "transparent"
                                border.color: netRow.modelData.connected ? win.theme.focus : "transparent"
                                Text {
                                    x: 10; anchors.verticalCenter: parent.verticalCenter
                                    width: parent.width - status.width - 30; elide: Text.ElideRight
                                    text: netRow.modelData.name; textFormat: Text.PlainText
                                    color: win.theme.ink; font.pixelSize: 13
                                }
                                Text {
                                    id: status
                                    anchors { right: parent.right; rightMargin: 10; verticalCenter: parent.verticalCenter }
                                    text: [W.stateLabel(netRow.modelData), W.signalLabel(netRow.modelData.signal),
                                           netRow.modelData.security === "open" ? "Open" : "Secured"].filter(x => x).join(" · ")
                                    color: win.theme.inkSecondary; font.pixelSize: 12
                                }
                                HoverHandler { id: netHover }
                                TapHandler { onTapped: win.wifiClick(netRow.modelData) }
                                Accessible.role: Accessible.Button
                                Accessible.name: netRow.modelData.name + ", " + status.text
                                Accessible.onPressAction: win.wifiClick(netRow.modelData)
                            }
                            Row {
                                visible: netRow.modelData.connected && win.selectedNetwork === netRow.modelData.name
                                spacing: 8; leftPadding: 10
                                TextButton { theme: win.theme; label: "Disconnect"; onClicked: { win.selectedNetwork = ""; netRow.modelData.network.disconnect() } }
                                TextButton { id: forgetButton; theme: win.theme; label: "Forget"
                                             onClicked: { win.selectedNetwork = ""; win.wifiError = "";
                                                          win.wifiNote = W.forgotten(netRow.modelData.name);
                                                          netRow.modelData.network.forget() } }
                            }
                        }
                    }
                    // The password box, outside the list: the list is rebuilt as signal strengths
                    // change, which must not lose what is being typed. The text goes straight to
                    // NetworkManager and is cleared.
                    Column {
                        visible: win.passwordFor !== ""
                        readonly property string network: win.passwordFor
                        onNetworkChanged: password.text = ""
                        width: parent.width; spacing: 6; leftPadding: 10
                        onVisibleChanged: if (visible) password.forceActiveFocus(); else password.text = ""
                        Rectangle {
                            width: parent.width - 10; height: 32; radius: 9
                            color: "transparent"; border.color: password.activeFocus ? win.theme.focus : win.theme.glassBorder
                            TextInput {
                                id: password
                                x: 10; width: parent.width - 20; anchors.verticalCenter: parent.verticalCenter
                                echoMode: reveal.checked ? TextInput.Normal : TextInput.Password
                                color: win.theme.ink; font.pixelSize: 13; clip: true
                                inputMethodHints: Qt.ImhSensitiveData | Qt.ImhNoPredictiveText
                                onAccepted: if (win.submitPassword(text)) text = ""
                                Keys.onEscapePressed: win.passwordFor = ""
                                Text {
                                    visible: password.text.length === 0
                                    text: "Password for " + win.passwordFor; textFormat: Text.PlainText
                                    color: win.theme.inkSecondary; font.pixelSize: 13
                                }
                                Accessible.role: Accessible.EditableText
                                Accessible.passwordEdit: !reveal.checked
                                Accessible.name: "Password for " + win.passwordFor
                            }
                        }
                        Row {
                            spacing: 8
                            TextButton { theme: win.theme; label: "Connect"; onClicked: if (win.submitPassword(password.text)) password.text = "" }
                            TextButton { id: reveal; theme: win.theme; property bool checked: false
                                         label: checked ? "Hide" : "Show"; onClicked: checked = !checked }
                            TextButton { theme: win.theme; label: "Cancel"; onClicked: { win.passwordFor = ""; win.wifiError = "" } }
                        }
                    }
                    Text {
                        visible: win.wifiError !== "" || win.wifiNote !== ""
                        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                        text: win.wifiError || win.wifiNote
                        color: win.wifiError ? win.theme.error : win.theme.inkSecondary; font.pixelSize: 13
                    }
                }
                // Storage: free space on / and /home, with a suggestion only when space is low.
                Column {
                    width: parent.width; spacing: 6
                    Text {
                        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                        text: "Storage: " + D.disk(win.volumes)
                        color: win.theme.ink; font.pixelSize: 13
                        Accessible.role: Accessible.StaticText
                        Accessible.name: text
                    }
                    TextButton {
                        theme: win.theme
                        visible: win.lowDisk && apps.installed("files")
                        label: "Show biggest files"
                        onClicked: win.openHomeFiles()
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
