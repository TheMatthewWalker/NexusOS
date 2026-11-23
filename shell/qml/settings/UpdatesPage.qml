import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/firmware.mjs" as F

// Settings, Updates. NexusOS: what Guardian's update check last found
// (GET /guardian/updates; updates are approved in Guardian's window), a check
// now, and the channel: stable (releases) or testing (pre-releases first).
// The channel is a root file written by nexus-update-channel through pkexec
// (nix/modules/updates.nix); moving is then a normal, forward-only update.
// Firmware: the computer's own firmware (UEFI/BIOS,
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

    // ---- NexusOS ----
    readonly property var bridge: host.bridge
    property var system: null
    property int systemId: -1
    property int checkId: -1
    property string channel: "stable"
    property bool followsRelease: true
    function refreshSystem() { if (bridge && bridge.online) systemId = bridge.api("GET", "/guardian/updates") }
    Connections {
        target: page.bridge
        function onResult(id, type, data) {
            if (id === page.systemId) page.system = data;
            else if (id === page.checkId) { page.say(data.message || "Checking for updates.", false); page.refreshSystem() }
        }
        function onFailed(id, message) { if (id === page.systemId || id === page.checkId) page.say(message, true) }
    }
    FileView { path: "/etc/nixos/nexusos-channel"; onLoaded: page.channel = text().trim() === "testing" ? "testing" : "stable" }
    FileView { path: "/etc/nixos/flake.nix"; onLoaded: page.followsRelease = text().indexOf('inputs.nexusos.url = "github:') >= 0 }
    Process {
        id: channelSetter
        stdout: StdioCollector { id: channelOut }
        onExited: code => {
            let r = {}; try { r = JSON.parse(channelOut.text || "{}") } catch (e) {}
            if (code === 0 && r.message) { page.channel = channelSetter.wanted; page.say(r.message, false) }
            else page.say(r.error || "The channel was not changed (the password was not given).", true);
        }
        property string wanted: ""
    }
    function setChannel(c) {
        if (channelSetter.running || (c !== "stable" && c !== "testing") || c === channel) return;
        channelSetter.wanted = c;
        channelSetter.command = [Quickshell.env("NEXUS_PKEXEC") || "/run/wrappers/bin/pkexec", "/run/current-system/sw/bin/nexus-update-channel", c];
        channelSetter.running = true;
    }

    function say(text, error) { message = text; messageIsError = !!error }
    function check() { if (!finder.running) { checked = false; finder.running = true; lister.running = true } }
    Component.onCompleted: { check(); refreshSystem() }

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
        theme: page.theme; label: "NexusOS"; highlighted: page.host.highlight === "system"
        detail: !page.system ? "…" : page.system.guardian ? page.system.guardian
            : (page.system.updates && page.system.updates.message ? page.system.updates.message + " "
               : page.system.lastCheck ? (page.system.lastCheck.message || "") + " " : "") +
              (page.system.lastCheck && page.system.lastCheck.when ? "Last checked " + page.system.lastCheck.when + ". " : "") +
              "Updates are built and tested first, and you approve each one; it is used from the next restart, and the one before stays if anything goes wrong."
        Button { theme: page.theme; label: "Check now"; enabled: !!page.bridge && page.bridge.online
                 onClicked: page.checkId = page.bridge.api("POST", "/guardian/updates/check", {}) }
    }
    SettingRow {
        visible: page.followsRelease
        theme: page.theme; label: "Channel"; highlighted: page.host.highlight === "channel"
        detail: page.channel === "testing"
            ? "Testing: new versions before they are released to everyone, to help find problems. Back to stable whenever you like: you then move on once stable is newer."
            : "Stable: tested releases. Testing gets new versions first, for people who like to help find problems."
        Choice { theme: page.theme; name: "Channel"; current: page.channel
                 options: [{ value: "stable", label: "Stable" }, { value: "testing", label: "Testing" }]
                 onChose: v => page.setChannel(v) }
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
