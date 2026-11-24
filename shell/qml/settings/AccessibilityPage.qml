import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/access.mjs" as Access

// Settings, Accessibility (feature 6): bigger text and pointer, high
// contrast, less motion, the Orca screen reader, Describe this (the local
// vision model describes a window, an area or the screen: Describe.qml) and
// voice control (push-to-talk and spoken replies: Voice.qml). Every control
// in the desktop is labelled for screen readers. Each says plainly when its
// model is still downloading or turned off.
Column {
    id: page
    property var theme
    property var host
    readonly property var desktop: host.desktop
    readonly property var voice: host.voice
    readonly property var describer: host.describer
    Component.onCompleted: { if (voice) voice.refresh(); if (describer) describer.refresh() }
    // Settings steps aside so the picture shows what the person meant, not this page.
    function describe(target) { page.host.visible = false; page.describer.describe(target, "", "nexus") }
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
        theme: page.theme; label: "Magnifier"; highlighted: page.host.highlight === "magnifier"
        detail: "Enlarges the screen around the pointer. Super+Plus zooms in, Super+Minus out, Super+Esc stops."
        Choice { theme: page.theme; name: "Magnifier"; current: page.desktop.zoom
                 options: Access.zoomLevels.map(v => ({ value: v, label: v === 1 ? "Off" : Math.round(v * 100) + "%" }))
                 onChose: v => page.desktop.set("zoom", v) }
    }
    SettingRow {
        theme: page.theme; label: "Colour filter"; highlighted: page.host.highlight === "colourfilter"
        detail: "Changes the colours of the whole screen: shades of grey, inverted, or easier to tell apart with colour blindness " +
                "(Red: red-blind, protanopia; Green: green-blind, deuteranopia, the most common; Blue: blue-blind, tritanopia)."
        Choice { theme: page.theme; name: "Colour filter"; current: page.desktop.colourFilter
                 options: Access.filters.map(f => ({ value: f.value, label: f.short }))
                 onChose: v => page.desktop.set("colourFilter", v) }
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
        theme: page.theme; label: "Describe this"; highlighted: page.host.highlight === "describe"
        detail: !page.describer || !page.describer.enabled ? "Describing pictures is turned off on this computer (it needs more memory)."
              : !page.describer.ready ? "The local vision model (" + page.describer.model + ") downloads once this computer is online."
              : page.describer.busy ? "Describing…"
              : "The local vision model describes a window, an area or the whole screen; the answer shows in Nexus. "
                + "The picture is taken only when you choose, stays on this computer and is deleted. Nexus can do this too."
        Row {
            spacing: 8
            Button { theme: page.theme; label: "A window or area"; enabled: !!page.describer && page.describer.ready && !page.describer.busy
                     onClicked: page.describe("area")
                     Accessible.name: "Describe a window or area: click a window or drag over an area" }
            Button { theme: page.theme; label: "The whole screen"; enabled: !!page.describer && page.describer.ready && !page.describer.busy
                     onClicked: page.describe("screen") }
        }
    }
    SettingRow {
        theme: page.theme; label: "Voice control"; highlighted: page.host.highlight === "voice"
        detail: !page.voice ? "" : page.voice.listenDownloading ? "The speech model is still downloading."
              : !page.voice.listenReady ? "Speech to text is not ready yet: its model downloads once this computer is online."
              : "Hold Super+V (or the microphone in Nexus) and speak; let go to ask. The microphone is open only while you hold it, "
                + "and a red bar shows it. Your words are turned into text on this computer."
        Switch { theme: page.theme; name: "Voice control"; checked: !!page.voice && page.voice.pushToTalk
                 onToggled: page.voice.setChoice("pushToTalk", !page.voice.pushToTalk) }
    }
    SettingRow {
        theme: page.theme; label: "Spoken replies"; highlighted: page.host.highlight === "spoken"
        detail: !page.voice ? "" : page.voice.speakDownloading ? "The voice is still downloading."
              : !page.voice.speakReady ? "The voice downloads once this computer is online."
              : "Nexus reads its answers aloud with a local voice. Super+V or Esc stops it."
        Switch { theme: page.theme; name: "Spoken replies"; checked: !!page.voice && page.voice.spokenReplies
                 onToggled: page.voice.setChoice("spokenReplies", !page.voice.spokenReplies) }
    }

    // Live captions for any sound (a video, a call): Live Captions from Flathub,
    // which turns speech into text on this computer (English).
    readonly property string captionsApp: "net.sapples.LiveCaptions"
    property bool captionsInstalled: false
    property string captionsNote: ""
    Process { id: captionsCheck; running: true; command: ["flatpak", "info", page.captionsApp]; onExited: (code) => page.captionsInstalled = code === 0 }
    Process {
        id: captionsInstall
        command: ["flatpak", "install", "--user", "--noninteractive", "-y", "flathub", page.captionsApp]
        onExited: (code) => { page.captionsNote = code === 0 ? "" : "Live Captions could not be installed: is this computer online?"; captionsCheck.running = true }
    }
    SettingRow {
        theme: page.theme; label: "Live captions"; highlighted: page.host.highlight === "captions"
        detail: page.captionsNote || (captionsInstall.running ? "Installing Live Captions…"
              : "Captions for anything you hear, a video or a call, as Windows' Live captions. Speech is turned into text on this computer; English only.")
        Button { theme: page.theme; primary: !page.captionsInstalled; enabled: !captionsInstall.running
                 label: page.captionsInstalled ? "Open" : "Install"
                 onClicked: page.captionsInstalled ? Quickshell.execDetached(["flatpak", "run", page.captionsApp]) : (captionsInstall.running = true) }
    }

    // An on-screen keyboard (wvkbd) for touchscreens, or for typing with a pointer.
    Process { id: keyboard; command: ["wvkbd-mobintl", "-L", "280"] }
    SettingRow {
        theme: page.theme; label: "On-screen keyboard"; highlighted: page.host.highlight === "osk"
        detail: keyboard.running ? "Showing along the bottom of the screen. Turn it off here when you are done."
                                 : "A keyboard on the screen, for a touchscreen or for typing with the mouse."
        Switch { theme: page.theme; name: "On-screen keyboard"; checked: keyboard.running; onToggled: keyboard.running = !keyboard.running }
    }
}
