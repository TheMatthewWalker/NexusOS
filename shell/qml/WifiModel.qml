import QtQuick
import Quickshell
import Quickshell.Networking
import "lib/wifi.mjs" as W

// Wi-Fi state and actions for Settings, Network (lib/wifi.mjs decides the
// order, what a click does and what to say). Connecting goes to
// NetworkManager, which keeps the password; the shell never stores it.
Item {
    id: wifi
    // Scan only while the Network page is showing, so a hidden page costs no radio time.
    property bool scanning: false

    readonly property var devices: Networking.devices ? Networking.devices.values : []
    readonly property bool hasWifi: Networking.backend !== NetworkBackendType.None && Networking.wifiHardwareEnabled
    readonly property bool on: hasWifi && Networking.wifiEnabled
    // The adapter NetworkManager manages. A machine can have others it leaves
    // alone (one running as an access point, say), which have no list.
    readonly property var device: devices.find(d => d.type === DeviceType.Wifi && d.nmManaged)
        || devices.find(d => d.type === DeviceType.Wifi) || null
    readonly property string ssid: {
        for (const d of devices)
            if (d.type === DeviceType.Wifi && d.networks)
                for (const n of d.networks.values) if (n.connected) return n.name;
        return "";
    }
    readonly property bool wired: devices.some(d => d.type === DeviceType.Wired && d.connected)
    Binding { when: wifi.device !== null; target: wifi.device; property: "scannerEnabled"; value: wifi.scanning && wifi.on }

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
    readonly property var rows: !on || !device || !device.networks ? [] : W.rows(device.networks.values.map(n => ({
        name: n.name, signal: n.signalStrength, securityName: securityNames[n.security] || "Unknown",
        security: W.securityKind(securityNames[n.security]), connected: n.connected, known: n.known,
        state: stateNames[n.state] || "Unknown", network: n })), 12)

    property string selected: ""            // a connected network whose Disconnect and Forget are showing
    property string passwordFor: ""         // the network whose password box is open
    property string passwordSecurity: ""
    property string error: ""
    property string note: ""

    function setEnabled(enabled) { Networking.wifiEnabled = enabled }
    function click(row) {
        error = ""; note = "";
        switch (W.clickAction(row)) {
        case "details": selected = selected === row.name ? "" : row.name; break;
        case "connect": passwordFor = ""; row.network.connect(); break;
        case "password": passwordFor = row.name; passwordSecurity = row.securityName; break;
        default: note = W.explanation(row);
        }
    }
    function submitPassword(text) {
        const problem = W.passwordProblem(text, passwordSecurity);
        if (problem) { error = problem; return false; }
        const row = rows.find(r => r.name === passwordFor);
        if (row) row.network.connectWithPsk(text);
        passwordFor = ""; error = "";
        return true;
    }
    function disconnect(row) { selected = ""; row.network.disconnect() }
    function forget(row) { selected = ""; error = ""; note = W.forgotten(row.name); row.network.forget() }

    // Failures are watched on Quickshell's own network list, whose objects
    // outlive the rows above.
    Instantiator {
        model: wifi.device ? wifi.device.networks : null
        delegate: Connections {
            required property var modelData
            target: modelData
            function onConnectionFailed(reason) {
                wifi.failed({ name: modelData.name, securityName: wifi.securityNames[modelData.security] || "Unknown",
                              security: W.securityKind(wifi.securityNames[modelData.security]) }, reason)
            }
        }
    }
    function failed(row, reason) {
        const name = failNames[reason] || "Unknown";
        // A saved network whose password changed, or a first try that was wrong: ask (again).
        if (row.security === "password" && name !== "WifiNetworkLost") { passwordFor = row.name; passwordSecurity = row.securityName; }
        error = row.name + ": " + W.failMessage(name);
    }

    readonly property string connectivity: Networking.canCheckConnectivity && Networking.connectivityCheckEnabled
        ? ({ [NetworkConnectivity.Full]: "full", [NetworkConnectivity.Limited]: "limited",
             [NetworkConnectivity.Portal]: "portal", [NetworkConnectivity.None]: "none" })[Networking.connectivity] || "unknown"
        : "unknown"
}
