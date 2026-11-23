import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/developer.mjs" as D

// Settings, Developer (nix/modules/developer.nix): SSH keys, project tools,
// containers, serial boards and virtual machines. Everything here runs as
// you, except turning on virtual machines, which is a Guardian change.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property string home: Quickshell.env("HOME") || ""
    property var keys: []
    property string message: ""
    property string lang: "python"
    function refresh() { lister.running = true }
    Component.onCompleted: refresh()

    Process { id: lister; command: ["sh", "-c", "for f in \"$HOME\"/.ssh/*.pub; do [ -f \"$f\" ] && { echo \"== $f\"; cat \"$f\"; }; done; true"]
              stdout: StdioCollector { onStreamFinished: page.keys = D.keys(text) } }
    // A new key for this computer: Ed25519, without a passphrase (the disk is
    // encrypted, and the keyring's agent keeps it for the session); never
    // replacing one that is there.
    Process {
        id: keygen
        command: ["sh", "-c", "mkdir -p \"$HOME/.ssh\" && chmod 700 \"$HOME/.ssh\" && [ ! -e \"$HOME/.ssh/id_ed25519\" ] && " +
                              "ssh-keygen -q -t ed25519 -N '' -C \"$(id -un)@$(cat /proc/sys/kernel/hostname)\" -f \"$HOME/.ssh/id_ed25519\""]
        onExited: code => { page.message = code === 0 ? "Key created. Copy it, then add it to GitHub, GitLab or the server." : "You already have a key (id_ed25519)."; page.refresh() }
    }
    Process { id: copier }
    function copy(text) { copier.command = ["wl-copy", "--", text]; copier.running = true; message = "Copied." }

    PageHeader { theme: page.theme; title: "Developer"; summary: "Tools for writing software: keys, projects, containers, boards and virtual machines." }
    Text { visible: page.message !== ""; text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13; width: page.width; wrapMode: Text.Wrap }

    SettingRow {
        theme: page.theme; label: "SSH keys"; highlighted: page.host.highlight === "ssh"
        detail: page.keys.length === 0 ? "None yet. A key lets you sign in to servers and push to GitHub or GitLab without a password."
              : "Your public keys: give these to services and servers. The private keys never leave this computer."
        Button { visible: !page.keys.some(k => k.file.endsWith("/id_ed25519.pub")); theme: page.theme; label: "Create a key"
                 onClicked: if (!keygen.running) keygen.running = true }
    }
    Repeater {
        model: page.keys
        delegate: Rectangle {
            required property var modelData
            width: page.width; height: 48; radius: 10; color: page.theme.chip
            Text { x: 16; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 140; elide: Text.ElideRight; textFormat: Text.PlainText
                   text: modelData.file.split("/").pop() + " · " + modelData.type + (modelData.comment ? " · " + modelData.comment : "")
                   color: page.theme.ink; font.pixelSize: 13 }
            Button { anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
                     theme: page.theme; label: "Copy"; onClicked: page.copy(modelData.key) }
        }
    }

    SettingRow {
        theme: page.theme; label: "Project tools"; highlighted: page.host.highlight === "projects"
        detail: "In a project folder, run the command below: the language's tools are then there whenever you are in that folder, and nowhere else. Or ask Nexus to set the project up."
    }
    Row {
        spacing: 8
        Choice { theme: page.theme; name: "Language"; current: page.lang
                 options: D.LANGUAGES.map(l => ({ value: l, label: ({ python: "Python", node: "Node", rust: "Rust", go: "Go", java: "Java", dotnet: ".NET", c: "C/C++" })[l] }))
                 onChose: v => page.lang = v }
        Button { theme: page.theme; label: "Copy command"; onClicked: page.copy(D.initCommand(page.lang)) }
    }
    SettingRow {
        theme: page.theme; label: "Containers"; highlighted: page.host.highlight === "containers"
        detail: "docker and docker compose work, on Podman: containers run as you, with no service running as root. Dev Containers in VS Code use the same."
    }
    SettingRow {
        theme: page.theme; label: "Boards and phones"; highlighted: page.host.highlight === "boards"
        detail: "Arduino, ESP32, Raspberry Pi Pico and other USB serial boards can be programmed without a password; adb and fastboot are installed for Android phones."
    }
    SettingRow {
        theme: page.theme; label: "Virtual machines"; highlighted: page.host.highlight === "vms"
        detail: "Run Windows or another system in a window (KVM, QEMU, virt-manager). Turning this on is a system change: Guardian builds and checks it first."
        Button { theme: page.theme; label: "Turn on…"; onClicked: page.host.proposeSystem(D.VM_PLAN) }
        Button { theme: page.theme; label: "Open"; onClicked: Quickshell.execDetached(["virt-manager"]) }
    }
}
