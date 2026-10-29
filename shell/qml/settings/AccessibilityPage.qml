import QtQuick
import Quickshell
import Quickshell.Io

// Settings, Accessibility (feature 6): bigger text and pointer, high
// contrast, less motion, and the Orca screen reader. Every control in the
// desktop is labelled for screen readers. "Describe this" (the local vision
// model) and voice control are not here yet, and the page says so.
Column {
    id: page
    property var theme
    property var host
    readonly property var desktop: host.desktop
    width: parent ? parent.width : 600
    spacing: 10

    property bool orcaRunning: false
    Process {
        id: orca
        command: ["orca", "--replace"]
        onRunningChanged: page.orcaRunning = running
    }
    Process { id: cursor }
    function setCursor(size) {
        desktop.set("cursorSize", size);
        cursor.command = [Quickshell.env("NEXUS_HYPRCTL") || "hyprctl", "setcursor", "Adwaita", String(size)];
        cursor.running = true;
    }

    PageHeader { theme: page.theme; title: "Accessibility"; summary: "Seeing, hearing and using NexusOS more easily." }

    SettingRow {
        theme: page.theme; label: "Bigger text and windows"; highlighted: page.host.highlight === "textsize"
        detail: "Scales everything on screen. The same as Display, Scale."
        Choice { theme: page.theme; name: "Size"; current: page.desktop.scale
                 options: [1, 1.25, 1.5, 2].map(v => ({ value: v, label: Math.round(v * 100) + "%" }))
                 onChose: v => page.desktop.set("scale", v) }
    }
    SettingRow {
        theme: page.theme; label: "High contrast"; highlighted: page.host.highlight === "contrast"
        detail: "Solid, darker panels and brighter text and borders in the desktop."
        Switch { theme: page.theme; name: "High contrast"; checked: page.desktop.highContrast
                 onToggled: page.desktop.set("highContrast", !page.desktop.highContrast) }
    }
    SettingRow {
        theme: page.theme; label: "Less motion"; highlighted: page.host.highlight === "motion"
        detail: "Turns off window animations. The Nexus core stays still too after the next login."
        Switch { theme: page.theme; name: "Less motion"; checked: !page.desktop.animations
                 onToggled: page.desktop.set("animations", !page.desktop.animations) }
    }
    SettingRow {
        theme: page.theme; label: "Pointer size"; highlighted: page.host.highlight === "pointer"
        Choice { theme: page.theme; name: "Pointer size"; current: page.desktop.cursorSize
                 options: [24, 32, 48, 64].map(v => ({ value: v, label: v === 24 ? "Normal" : v + " px" }))
                 onChose: v => page.setCursor(v) }
    }
    SettingRow {
        theme: page.theme; label: "Screen reader"; highlighted: page.host.highlight === "reader"
        detail: page.orcaRunning ? "Orca is reading the screen. Super+Alt+S turns it off." : "Orca reads out what is on screen and what you type."
        Switch { theme: page.theme; name: "Screen reader"; checked: page.orcaRunning
                 onToggled: { if (orca.running) orca.running = false; else orca.running = true } }
    }
    SettingRow {
        theme: page.theme; label: "Describe this, and voice control"
        detail: "Coming later: describing pictures and the screen with a local vision model, and controlling the desktop by voice."
    }
}
