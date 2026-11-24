import QtQuick
import Quickshell
import Quickshell.Io

// Settings, Mouse and touchpad: kept in desktop.json (DesktopSettings.qml) and
// applied to Hyprland at once; each change can be undone in the Timeline.
Column {
    id: page
    property var theme
    property var host
    readonly property var desktop: host.desktop
    width: parent ? parent.width : 600
    spacing: 10

    PageHeader { theme: page.theme; title: "Mouse and touchpad"; summary: "How the pointer moves, clicking and scrolling." }

    SettingRow {
        theme: page.theme; label: "Pointer speed"; highlighted: page.host.highlight === "pointerspeed"
        detail: page.desktop.pointerSpeed === 0 ? "Normal" : page.desktop.pointerSpeed < 0 ? "Slower" : "Faster"
        Slider { theme: page.theme; name: "Pointer speed"; value: (page.desktop.pointerSpeed + 1) / 2
                 onMoved: v => page.desktop.set("pointerSpeed", Math.round((v * 2 - 1) * 10) / 10) }
    }
    SettingRow {
        theme: page.theme; label: "Pointer acceleration"; highlighted: page.host.highlight === "acceleration"
        detail: "The pointer goes further when you move faster. Off is steadier for games and drawing."
        Switch { theme: page.theme; name: "Pointer acceleration"; checked: page.desktop.pointerAcceleration
                 onToggled: page.desktop.set("pointerAcceleration", !page.desktop.pointerAcceleration) }
    }
    SettingRow {
        theme: page.theme; label: "Left-handed"; highlighted: page.host.highlight === "lefthanded"
        detail: "Swaps the left and right buttons."
        Switch { theme: page.theme; name: "Left-handed"; checked: page.desktop.leftHanded
                 onToggled: page.desktop.set("leftHanded", !page.desktop.leftHanded) }
    }
    SettingRow {
        theme: page.theme; label: "Mouse wheel scrolls like a touchscreen"; highlighted: page.host.highlight === "mousescroll"
        detail: "Natural scrolling for the mouse: the page moves the way the wheel turns."
        Switch { theme: page.theme; name: "Mouse natural scrolling"; checked: page.desktop.mouseNaturalScroll
                 onToggled: page.desktop.set("mouseNaturalScroll", !page.desktop.mouseNaturalScroll) }
    }

    SettingRow {
        theme: page.theme; label: "Tap to click"; highlighted: page.host.highlight === "taptoclick"
        detail: "On a touchpad: tap with one finger to click, two to right-click, three to middle-click."
        Switch { theme: page.theme; name: "Tap to click"; checked: page.desktop.tapToClick
                 onToggled: page.desktop.set("tapToClick", !page.desktop.tapToClick) }
    }
    SettingRow {
        theme: page.theme; label: "Natural scrolling"; highlighted: page.host.highlight === "naturalscroll"
        detail: "On a touchpad: the page follows your fingers, as on a phone. Off: as with a scroll bar."
        Switch { theme: page.theme; name: "Natural scrolling"; checked: page.desktop.naturalScroll
                 onToggled: page.desktop.set("naturalScroll", !page.desktop.naturalScroll) }
    }
    SettingRow {
        theme: page.theme; label: "Scrolling speed"; highlighted: page.host.highlight === "scrollspeed"
        detail: Math.round(page.desktop.scrollSpeed * 100) + "%"
        Slider { theme: page.theme; name: "Scrolling speed"; value: (page.desktop.scrollSpeed - 0.5) / 1.5
                 onMoved: v => page.desktop.set("scrollSpeed", Math.round((0.5 + v * 1.5) * 10) / 10) }
    }
    SettingRow {
        theme: page.theme; label: "Gestures"; highlighted: page.host.highlight === "gestures"
        detail: "Swipe three fingers left or right to move between workspaces. Two fingers scroll; pinch zooms in apps that support it."
    }

    // Pen tablets (Wacom, Huion, XP-Pen): libinput drives them; here, which screen the pen covers.
    property bool hasTablet: false
    Process {
        running: true
        command: [Quickshell.env("NEXUS_HYPRCTL") || "hyprctl", "devices", "-j"]
        stdout: StdioCollector { onStreamFinished: { try { page.hasTablet = (JSON.parse(text).tablets || []).length > 0 } catch (e) {} } }
    }
    SettingRow {
        visible: page.hasTablet || page.desktop.tabletScreen !== ""
        theme: page.theme; label: "Pen tablet"; highlighted: page.host.highlight === "tablet"
        detail: "Which screen the tablet's drawing area covers. One screen keeps the pen's proportions right on a desk with two."
        Choice { theme: page.theme; name: "Pen tablet screen"; current: page.desktop.tabletScreen
                 options: [{ value: "", label: "All screens" }].concat(page.desktop.monitors.filter(m => !m.disabled)
                          .map(m => ({ value: m.name, label: m.builtIn ? "Built-in" : m.label })))
                 onChose: v => page.desktop.set("tabletScreen", v) }
    }
}
