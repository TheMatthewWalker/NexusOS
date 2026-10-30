import QtQuick
import Quickshell
import Quickshell.Io

// Settings, Display: brightness (brightnessctl, for built-in screens), night
// light (hyprsunset) and the screen scale. Scale and night light are kept in
// desktop.json (DesktopSettings.qml) and come back at every login.
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

    PageHeader { theme: page.theme; title: "Display"; summary: Quickshell.screens.map(s => s.name + " · " + s.width + " × " + s.height).join(", ") }

    SettingRow {
        theme: page.theme; label: "Brightness"; highlighted: page.host.highlight === "brightness"
        detail: page.backlight === "" ? "This screen's brightness is set on the screen itself."
              : Math.round(page.brightness * 100) + "%"
        Slider { theme: page.theme; name: "Brightness"; visible: page.backlight !== ""; value: page.brightness || 0
                 onMoved: v => page.applyBrightness(v) }
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
}
