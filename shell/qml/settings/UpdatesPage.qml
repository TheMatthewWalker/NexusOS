import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/firmware.mjs" as F

// Settings, Updates. Firmware: the computer's own firmware (UEFI/BIOS,
// Thunderbolt, docks, SSDs, mice and keyboards) from the Linux Vendor
// Firmware Service, through fwupd (nix/modules/firmware.nix). fwupdmgr runs as
// you; fwupd asks for an administrator's password before it writes firmware,
// and only updates on mains power. A device id is checked before it is passed on.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    property var firmware: []
    property var devices: []
    property bool checked: false
    property string message: ""
    property bool messageIsError: false
    property bool needsRestart: false
    property string expanded: ""

    function say(text, error) { message = text; messageIsError = !!error }
    function check() { if (!finder.running) { checked = false; finder.running = true; lister.running = true } }
    Component.onCompleted: check()

    Process {
        id: finder
        command: ["fwupdmgr", "get-updates", "--json"]
        stdout: StdioCollector { onStreamFinished: { page.firmware = F.updates(text); page.checked = true } }
    }
    Process { id: lister; command: ["fwupdmgr", "get-devices", "--json"]; stdout: StdioCollector { onStreamFinished: page.devices = F.devices(text) } }
    Process {
        id: refresher
        command: ["fwupdmgr", "refresh", "--force"]
        onExited: page.check()
    }
    Process {
        id: updater
        property bool restartAfter: false
        stderr: StdioCollector { id: updaterErr }
        onExited: code => {
            const r = F.result(code, updaterErr.text);
            if (!r.ok) { page.say(r.message, true); page.check(); return }
            page.needsRestart = page.needsRestart || restartAfter;
            page.say(restartAfter ? "Ready. The update finishes when you restart; keep the charger plugged in until it is done."
                                  : "Firmware updated.", false);
            page.check();
        }
    }
    function update(items) {
        const ok = items.filter(u => /^[0-9a-f]{40}$/.test(u.id));
        if (updater.running || ok.length === 0) return;
        updater.restartAfter = ok.some(u => u.restart);
        updater.command = ["fwupdmgr", "update", "--assume-yes", "--no-reboot-check"].concat(ok.map(u => u.id));
        updater.running = true;
        say("Updating firmware. Do not switch the computer off or unplug the device.", false);
    }

    PageHeader { theme: page.theme; title: "Updates"; summary: "Keep NexusOS and your computer's firmware up to date." }

    Text {
        visible: page.message !== ""
        width: page.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.message; color: page.messageIsError ? page.theme.error : page.theme.inkSecondary; font.pixelSize: 13
    }
    Button {
        visible: page.needsRestart
        theme: page.theme; label: "Restart now"
        onClicked: Quickshell.execDetached(["systemctl", "reboot"])
    }

    SettingRow {
        theme: page.theme; label: "Firmware"; highlighted: page.host.highlight === "firmware"
        detail: F.summary(page.firmware, page.checked) +
            " Firmware comes from the device makers, through the Linux Vendor Firmware Service; an administrator's password is asked before anything is written."
        Button {
            visible: page.firmware.length > 1
            theme: page.theme; label: updater.running ? "Updating…" : "Update all"
            onClicked: page.update(page.firmware)
        }
        Button {
            theme: page.theme; label: refresher.running || finder.running ? "Checking…" : "Check now"
            onClicked: if (!refresher.running) refresher.running = true
        }
    }
    Repeater {
        model: page.firmware
        delegate: Rectangle {
            required property var modelData
            readonly property bool open: page.expanded === modelData.id
            width: page.width; radius: 10
            height: info.height + 24
            color: page.theme.chip
            border.color: modelData.urgent ? page.theme.focus : "transparent"
            Column {
                id: info
                x: 16; y: 12; width: parent.width - 200; spacing: 4
                Text { width: parent.width; wrapMode: Text.Wrap; text: F.line(modelData); color: page.theme.ink; font.pixelSize: 14; textFormat: Text.PlainText }
                Text {
                    width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                    text: open ? (modelData.notes || modelData.summary) : modelData.summary
                    color: page.theme.inkSecondary; font.pixelSize: 12
                }
            }
            Row {
                anchors { right: parent.right; rightMargin: 12; top: parent.top; topMargin: 10 }
                spacing: 8
                Button { theme: page.theme; label: open ? "Less" : "What's new"; onClicked: page.expanded = open ? "" : modelData.id }
                Button { theme: page.theme; label: "Update"; onClicked: page.update([modelData]) }
            }
        }
    }
    Text {
        visible: page.checked && page.firmware.length === 0 && page.devices.length > 0
        width: page.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: "Up to date: " + page.devices.map(d => d.name + (d.version ? " " + d.version : "")).join(", ") + "."
        color: page.theme.inkSecondary; font.pixelSize: 12
    }
}
