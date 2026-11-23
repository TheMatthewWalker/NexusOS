import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Hyprland
import "lib/region.mjs" as R
import "lib/displays.mjs" as Mon

// The person's desktop choices from Settings, kept in
// ~/.config/nexusos/desktop.json and applied to Hyprland at run time with
// hyprctl. None of them needs a system change: they are per person, take
// effect at once and are applied again at every login.
//   windows     "floating" (default, like Windows or macOS) or "tiling"
//   blur, animations   Hyprland decoration and animations
//   scale       the screen scale (1, 1.25, 1.5, 1.75 or 2)
//   nightLight, warmth   hyprsunset's colour temperature, in kelvin
// The NixOS options (services.nexus-desktop.*) give the starting values.
Item {
    id: desktop
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    readonly property string hyprsunset: Quickshell.env("NEXUS_HYPRSUNSET") || "hyprsunset"
    readonly property string configDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos"

    property alias windows: store.windows
    property alias blur: store.blur
    property alias animations: store.animations
    property alias scale: store.scale
    property alias nightLight: store.nightLight
    property alias warmth: store.warmth
    property alias highContrast: store.highContrast
    property alias cursorSize: store.cursorSize
    property alias doNotDisturb: store.doNotDisturb
    property alias screenOffMinutes: store.screenOffMinutes
    property alias sleepBatteryMinutes: store.sleepBatteryMinutes
    property alias sleepPluggedMinutes: store.sleepPluggedMinutes
    property alias lidAction: store.lidAction
    property alias keyboardLayouts: store.keyboardLayouts
    property alias monitorLayout: store.monitorLayout
    property alias crashNotices: store.crashNotices
    property var monitors: []            // the screens connected now (hyprctl monitors all -j), for Settings, Display
    readonly property string baseLayout: Quickshell.env("NEXUS_KEYBOARD_LAYOUT") || "us"
    // False while game mode has blur and animations off, so applying settings does not undo it.
    property bool gameMode: false
    // Each change goes in the activity journal with its old value, so the Timeline can undo it.
    property var bridge: null
    function describe(key, value) {
        switch (key) {
        case "windows": return "Windows: " + (value === "tiling" ? "tiled" : "floating");
        case "blur": return "Blur " + (value ? "on" : "off");
        case "animations": return "Animations " + (value ? "on" : "off");
        case "scale": return "Screen scale " + Math.round(value * 100) + "%";
        case "nightLight": return "Night light " + (value ? "on" : "off");
        case "warmth": return "Night light warmth " + value + " K";
        case "highContrast": return "High contrast " + (value ? "on" : "off");
        case "cursorSize": return "Pointer size " + value;
        case "doNotDisturb": return "Do not disturb " + (value ? "on" : "off");
        case "screenOffMinutes": return "Screen off after " + (value ? value + " minutes" : "never");
        case "sleepBatteryMinutes": return "Sleep on battery after " + (value ? value + " minutes" : "never");
        case "sleepPluggedMinutes": return "Sleep plugged in after " + (value ? value + " minutes" : "never");
        case "keyboardLayouts": return "Keyboard layouts: " + [baseLayout].concat(value ? value.split(",") : []).join(", ");
        case "monitorLayout": return "Screen arrangement";
        case "crashNotices": return "Crash notices " + (value ? "on" : "off");
        case "lidAction": return "Closing the lid: " + ({ sleep: "sleep", lock: "lock the screen", nothing: "do nothing" })[value];
        default: return key;
        }
    }

    function set(key, value, fromUndo) {
        if (store[key] === value) return;
        const wasTiling = store.windows === "tiling";
        const before = store[key];
        if (bridge && bridge.online && !fromUndo && key !== "warmth")
            bridge.api("POST", "/journal", { kind: "setting", summary: describe(key, value), before: { value: before },
                                             after: { value: value }, reversible: true, ref: "desktop:" + key });
        store[key] = value;
        file.writeAdapter();
        if (key === "windows") applyWindows(wasTiling);
        else if (key === "keyboardLayouts" && !value) run(["keyword", "input:kb_layout", baseLayout]);
        else if (key === "monitorLayout") readMonitors();
        else { apply(); if (key === "scale") readMonitors() }
    }

    FileView {
        id: file
        path: desktop.configDir + "/desktop.json"
        blockLoading: true
        // Applied once the file is read; a missing file means first login:
        // write the defaults, which also creates the folder.
        onLoaded: desktop.applyAll()
        onLoadFailed: error => { if (error === FileViewError.FileNotFound) writeAdapter(); desktop.applyAll() }
        adapter: JsonAdapter {
            id: store
            property string windows: Quickshell.env("NEXUS_WINDOWS") === "tiling" ? "tiling" : "floating"
            property bool blur: Quickshell.env("NEXUS_BLUR") !== "0"
            property bool animations: Quickshell.env("NEXUS_REDUCED_MOTION") !== "1"
            property real scale: 1
            property bool nightLight: false
            property int warmth: 4500
            property bool highContrast: false
            property int cursorSize: 24
            property bool doNotDisturb: false
            property int screenOffMinutes: 5
            property int sleepBatteryMinutes: 15
            property int sleepPluggedMinutes: 30
            property string lidAction: "sleep"
            property string keyboardLayouts: ""    // extra XKB layouts, "fr,de"; Alt+Shift switches
            property string monitorLayout: "{}"    // lib/displays.mjs: where each screen goes, its mode and scale
            property bool crashNotices: false      // say when an app crashes (CrashWatcher.qml); off until chosen
        }
    }

    Process { id: hypr }
    function run(args) {
        // Each change is a separate short hyprctl call; queue them so none is lost.
        queue.push(args);
        if (!hypr.running) next();
    }
    property var queue: []
    function next() {
        if (queue.length === 0) return;
        hypr.command = [hyprctl].concat(queue.shift());
        hypr.running = true;
    }
    Connections { target: hypr; function onExited() { desktop.next() } }

    readonly property string floatRules: "keyword windowrule float on, match:class .* ; keyword windowrule center on, match:class .*"
    function keywords() {
        const k = [];
        if (!gameMode) {
            k.push("keyword decoration:blur:enabled " + store.blur);
            k.push("keyword animations:enabled " + store.animations);
        }
        if (store.scale !== 1) k.push("keyword monitor ,preferred,auto," + store.scale);
        const kb = R.keyboard(baseLayout, store.keyboardLayouts);
        if (kb) k.push("keyword input:kb_layout " + kb.layout, "keyword input:kb_options " + kb.options);
        return k;
    }
    function apply() { run(["--batch", keywords().join(" ; ")]) }
    // Floating is a window rule added at run time. Going back to tiling needs
    // the rule gone: reload Hyprland's own configuration, then apply the rest
    // again. Windows that are already open follow the new mode too.
    function applyWindows(wasTiling) {
        const floating = store.windows === "floating";
        if (!floating && !wasTiling) run(["reload"]);
        run(["--batch", (floating ? [floatRules] : []).concat(keywords()).join(" ; ")]);
        for (const t of Hyprland.toplevels.values) {
            if (!t.address) continue;
            const address = t.address.startsWith("0x") ? t.address : "0x" + t.address;
            run(["dispatch", (floating ? "setfloating" : "settiled") + " address:" + address]);
        }
    }
    // Screens: read what is connected, then apply the arrangement. Again
    // whenever a screen is plugged in or out.
    Process {
        id: monitorReader
        command: [desktop.hyprctl, "monitors", "all", "-j"]
        stdout: StdioCollector {
            onStreamFinished: {
                desktop.monitors = Mon.parse(text);
                let layout = {};
                try { layout = JSON.parse(store.monitorLayout || "{}") } catch (e) {}
                if (desktop.monitors.length < 2 && Object.keys(layout).length === 0) return;
                const r = Mon.rules(desktop.monitors, layout, store.scale);
                if (r.length) desktop.run(["--batch", r.map(x => "keyword " + x).join(" ; ")]);
            }
        }
    }
    function readMonitors() { if (!monitorReader.running) monitorReader.running = true; else monitorAgain.restart() }
    Timer { id: monitorAgain; interval: 600; onTriggered: desktop.readMonitors() }
    Connections { target: Quickshell; function onScreensChanged() { monitorAgain.restart() } }

    property bool applied: false
    function applyAll() {
        if (applied) return;
        applied = true;
        run(["--batch", (store.windows === "floating" ? [floatRules] : []).concat(keywords()).join(" ; ")]);
        if (store.cursorSize !== 24) run(["setcursor", "Adwaita", String(store.cursorSize)]);
        readMonitors();
    }

    // Night light: hyprsunset runs while it is on; changing the warmth restarts it.
    Process {
        id: sunset
        running: store.nightLight
        command: [desktop.hyprsunset, "-t", String(store.warmth)]
    }
    onWarmthChanged: if (sunset.running) { sunset.running = false; restartSunset.start() }
    Timer { id: restartSunset; interval: 150; onTriggered: sunset.running = store.nightLight }
}
