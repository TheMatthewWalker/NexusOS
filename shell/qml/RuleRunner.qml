import QtQuick
import Quickshell
import Quickshell.Wayland
import Quickshell.Services.Pipewire
import Quickshell.Services.UPower

// Automations (feature 9), the shell's half. Core holds the rules and decides
// which fire (RuleEngine.cs); the shell reports what it sees and performs the
// actions Core hands back. Every action here is personal and reversible:
// opening an app, a personal setting, a notification. Nothing reaches the
// system.
//   events: app.opened, network.joined, power.plugged/unplugged,
//           device.connected/disconnected (audio outputs, displays)
//   actions: app.open, setting.set (volume, night-light, wallpaper), notify
Item {
    id: runner
    property var bridge
    property var desktop
    property var wallpaper
    property var apps
    property var wifi
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    property var eventIds: ({})
    property real cursor: 0
    property int pendingId: -1

    function report(ev) {
        if (!bridge || !bridge.online) return;
        const id = bridge.api("POST", "/events", ev);
        if (id >= 0) { const m = eventIds; m[id] = true; eventIds = m }
    }
    Connections {
        target: runner.bridge
        function onResult(id, type, data) {
            if (runner.eventIds[id]) {
                delete runner.eventIds[id];
                for (const f of data.fired || []) runner.perform(f.ruleName, f.actions);
            } else if (id === runner.pendingId) {
                runner.cursor = data.next || runner.cursor;
                for (const item of data.items || []) runner.perform(item.ruleName, item.actions);
            }
        }
        function onFailed(id, message) { if (runner.eventIds[id]) delete runner.eventIds[id] }
    }
    // Time triggers are queued by Core's own timer; collect them.
    Timer {
        interval: 30000; running: true; repeat: true
        onTriggered: if (runner.bridge && runner.bridge.online) runner.pendingId = runner.bridge.api("GET", "/rules/pending-actions?after=" + runner.cursor)
    }

    function notify(text) { Quickshell.execDetached([hyprctl, "notify", "-1", "6000", "rgb(86a8ff)", text]) }
    function perform(ruleName, actions) {
        for (const a of actions || []) {
            switch (a.type) {
            case "app.open":
                if (!apps.open(a.appId)) {
                    const entry = DesktopEntries.byId(String(a.appId).replace(/\.desktop$/, "")) || DesktopEntries.heuristicLookup(a.appId);
                    if (entry) entry.execute(); else notify(ruleName + ": " + a.appId + " is not installed");
                }
                break;
            case "setting.set":
                setSetting(a.key, a.value, ruleName);
                break;
            case "notify":
                notify(a.text || ruleName);
                break;
            default:
                notify(ruleName + ": " + a.type + " is not available yet");
            }
        }
    }
    function setSetting(key, value, ruleName) {
        const sink = Pipewire.defaultAudioSink;
        if (key === "volume" && sink && sink.audio) sink.audio.volume = Math.max(0, Math.min(1, Number(value) / 100));
        else if (key === "night-light") desktop.set("nightLight", value === true || value === "on");
        else if (key === "wallpaper" && typeof value === "string") wallpaper.set(value);
        else notify(ruleName + ": the " + key + " setting is not available yet");
    }

    // ---- what the shell observes ----
    // Apps opening: each new window's app id, once per app while it stays open.
    property var openApps: []
    Connections {
        target: ToplevelManager.toplevels
        function onValuesChanged() {
            const now = ToplevelManager.toplevels.values.map(t => t.appId).filter(x => x);
            for (const id of now) if (!runner.openApps.includes(id)) runner.report({ type: "app.opened", appId: id });
            runner.openApps = now;
        }
    }
    // Joining a Wi-Fi network.
    Connections {
        target: runner.wifi
        function onSsidChanged() { if (runner.wifi.ssid) runner.report({ type: "network.joined", ssid: runner.wifi.ssid }) }
    }
    // Plugging in and unplugging the charger.
    Connections {
        target: UPower
        function onOnBatteryChanged() { runner.report({ type: UPower.onBattery ? "power.unplugged" : "power.plugged" }) }
    }
    // Audio outputs (headphones, speakers) and displays coming and going.
    property var outputs: []
    Connections {
        target: Pipewire.nodes
        function onValuesChanged() {
            const now = Pipewire.nodes.values.filter(n => n.isSink && !n.isStream).map(n => n.description || n.name);
            for (const n of now) if (!runner.outputs.includes(n)) runner.report({ type: "device.connected", kind: "audio", name: n });
            for (const n of runner.outputs) if (!now.includes(n)) runner.report({ type: "device.disconnected", kind: "audio", name: n });
            runner.outputs = now;
        }
    }
    property int screens: Quickshell.screens.length
    onScreensChanged: { report({ type: screens > lastScreens ? "device.connected" : "device.disconnected", kind: "display" }); lastScreens = screens }
    property int lastScreens: Quickshell.screens.length
    Component.onCompleted: {
        outputs = Pipewire.nodes.values.filter(n => n.isSink && !n.isStream).map(n => n.description || n.name);
        openApps = ToplevelManager.toplevels.values.map(t => t.appId).filter(x => x);
    }
}
