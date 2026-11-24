import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/displays.mjs" as D

// Settings, Display: brightness (brightnessctl, for built-in screens), night
// light (hyprsunset) and the screen scale. Scale and night light are kept in
// desktop.json (DesktopSettings.qml) and come back at every login.
// Screens: with more than one, where each goes (lib/displays.mjs), its
// resolution and scale. A change is undone after 15 seconds unless it is
// kept, so a screen that shows nothing never leaves you stuck.
Column {
    id: page
    property var theme
    property var host
    readonly property var desktop: host.desktop
    width: parent ? parent.width : 600
    spacing: 10

    // brightnessctl -m prints: device,class,current,percent%,max
    property real brightness: NaN
    property string backlight: ""
    readonly property string brightnessctl: Quickshell.env("NEXUS_BRIGHTNESSCTL") || "brightnessctl"
    Process {
        id: readBrightness
        running: true
        command: [page.brightnessctl, "-m", "-c", "backlight", "info"]
        stdout: StdioCollector {
            onStreamFinished: {
                const f = text.trim().split(",");
                if (f.length >= 5) { page.backlight = f[0]; page.brightness = parseInt(f[3]) / 100 }
            }
        }
    }
    Process { id: setBrightness }
    function applyBrightness(v) {
        brightness = Math.max(0.05, v);           // never fully black: the screen must stay readable
        setBrightness.command = [brightnessctl, "-d", backlight, "set", Math.round(brightness * 100) + "%"];
        setBrightness.running = true;
    }

    // The arrangement being tried, and the one to go back to.
    readonly property var layout: { try { return JSON.parse(desktop.monitorLayout || "{}") } catch (e) { return {} } }
    property string previous: ""
    property int countdown: 0
    function changeScreen(m, field, value) {
        const next = JSON.stringify(D.change(layout, desktop.monitors, m, field, value));
        if (next === desktop.monitorLayout) return;
        if (countdown === 0) previous = desktop.monitorLayout;
        countdown = 15;
        desktop.set("monitorLayout", next);
    }
    function keep() { countdown = 0; previous = "" }
    function revert() { countdown = 0; if (previous !== "") desktop.set("monitorLayout", previous); previous = "" }
    Timer { interval: 1000; repeat: true; running: page.countdown > 0
            onTriggered: { page.countdown -= 1; if (page.countdown === 0) page.revert() } }
    Component.onCompleted: desktop.readMonitors()
    Component.onDestruction: if (countdown > 0) revert()

    PageHeader { theme: page.theme; title: "Display"; summary: Quickshell.screens.map(s => s.name + " · " + s.width + " × " + s.height).join(", ") }

    SettingRow {
        theme: page.theme; label: "Brightness"; highlighted: page.host.highlight === "brightness"
        detail: page.backlight === "" ? "This screen's brightness is set on the screen itself."
              : Math.round(page.brightness * 100) + "%"
        Slider { theme: page.theme; name: "Brightness"; visible: page.backlight !== ""; value: page.brightness || 0
                 onMoved: v => page.applyBrightness(v) }
    }
    SettingRow {
        theme: page.theme; label: "Variable refresh rate"; highlighted: page.host.highlight === "vrr"
        detail: "FreeSync or G-Sync on screens that have it: the screen keeps pace with the game, without tearing or stutter. " +
                "If a screen flickers, choose Off."
        Choice { theme: page.theme; name: "Variable refresh rate"; current: page.desktop.vrr
                 options: [{ value: "off", label: "Off" }, { value: "fullscreen", label: "Full-screen games" }, { value: "always", label: "Always" }]
                 onChose: v => page.desktop.set("vrr", v) }
    }
    SettingRow {
        theme: page.theme; label: "Night light"; highlighted: page.host.highlight === "nightlight"
        detail: page.desktop.nightLight ? "On · warmer colours are easier on the eyes in the evening" : "Off"
        Switch { theme: page.theme; name: "Night light"; checked: page.desktop.nightLight
                 onToggled: page.desktop.set("nightLight", !page.desktop.nightLight) }
    }
    SettingRow {
        theme: page.theme; label: "Warmth"; visible: page.desktop.nightLight
        detail: page.desktop.warmth + " K"
        // 6500 K is daylight; 2500 K is candle-warm.
        Slider { theme: page.theme; name: "Warmth"; value: (6500 - page.desktop.warmth) / 4000
                 onMoved: v => page.desktop.set("warmth", Math.round((6500 - v * 4000) / 100) * 100) }
    }
    SettingRow {
        theme: page.theme; label: "Scale"; highlighted: page.host.highlight === "scale"
        detail: "Makes text and windows bigger. 100% shows the most."
        Choice { theme: page.theme; name: "Scale"; current: page.desktop.scale
                 options: [1, 1.25, 1.5, 1.75, 2].map(v => ({ value: v, label: Math.round(v * 100) + "%" }))
                 onChose: v => page.desktop.set("scale", v) }
    }

    SettingRow {
        visible: page.countdown > 0
        theme: page.theme; label: "Keep this arrangement?"
        detail: "Going back to the one before in " + page.countdown + " seconds."
        Button { theme: page.theme; label: "Keep"; onClicked: page.keep() }
        Button { theme: page.theme; label: "Go back"; onClicked: page.revert() }
    }
    SettingRow {
        visible: page.desktop.monitors.length > 1
        theme: page.theme; label: "Screens"; highlighted: page.host.highlight === "screens"
        detail: "Where each screen is, around the main one. Remembered for each screen, so it comes back when you plug it in again."
    }
    Repeater {
        model: page.desktop.monitors.length > 1 ? page.desktop.monitors : []
        delegate: Rectangle {
            id: screenCard
            required property var modelData
            readonly property bool isMain: D.main(page.desktop.monitors, page.layout).key === modelData.key
            readonly property string place: isMain ? "main" : (D.current(page.layout, modelData, "place") || "right")
            width: page.width; radius: 12
            height: screenColumn.height + 24
            color: page.theme.chip
            border.color: isMain ? page.theme.focus : "transparent"
            Column {
                id: screenColumn
                x: 16; y: 12; width: parent.width - 32; spacing: 8
                Text { text: modelData.label + (modelData.builtIn ? " (built in)" : "") + (isMain ? " · main" : "")
                       color: page.theme.ink; font.pixelSize: 14; textFormat: Text.PlainText }
                Choice {
                    theme: page.theme; name: modelData.label + " position"; current: screenCard.place
                    options: D.PLACES.map(p => ({ value: p, label: D.placeLabel(p) }))
                    onChose: v => page.changeScreen(modelData, "place", v)
                }
                Choice {
                    visible: screenCard.place !== "off" && modelData.modes.length > 1
                    theme: page.theme; name: modelData.label + " resolution"
                    current: D.current(page.layout, modelData, "mode") || (modelData.modes[0] || {}).value
                    options: modelData.modes
                    onChose: v => page.changeScreen(modelData, "mode", v)
                }
                Choice {
                    visible: screenCard.place !== "off"
                    theme: page.theme; name: modelData.label + " scale"
                    current: D.current(page.layout, modelData, "scale") || page.desktop.scale
                    options: D.SCALES.map(v => ({ value: v, label: Math.round(v * 100) + "%" }))
                    onChose: v => page.changeScreen(modelData, "scale", v)
                }
            }
        }
    }
}
