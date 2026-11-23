import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/describe.mjs" as D
import "../lib/secureboot.mjs" as SB

// Settings, About: this computer and its storage. Secure Boot is turned on
// here in steps (scripts/boot/nexus_secure_boot.py): a Guardian change, the
// firmware's Setup Mode, putting the keys in (root, through pkexec), and
// switching it on in the firmware.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    property var info: ({})
    // One small script, so the page needs no extra services: os-release, the
    // host name, the CPU, memory, graphics and the kernel, one "key=value" a line.
    Process {
        running: true
        command: ["sh", "-c",
            ". /etc/os-release 2>/dev/null; echo \"os=$PRETTY_NAME\"; echo \"host=$(cat /proc/sys/kernel/hostname)\"; " +
            "echo \"cpu=$(sed -n 's/^model name[^:]*: //p' /proc/cpuinfo | head -1)\"; " +
            "echo \"threads=$(nproc)\"; echo \"memory=$(sed -n 's/^MemTotal: *\\([0-9]*\\).*/\\1/p' /proc/meminfo)\"; " +
            "echo \"kernel=$(uname -r)\"; " +
            "command -v lspci >/dev/null && lspci | sed -n 's/.*\\(VGA\\|3D\\)[^:]*: //p' | sed 's/^/gpu=/'"]
        stdout: StdioCollector {
            onStreamFinished: {
                const info = { gpu: [] };
                for (const line of text.split("\n")) {
                    const i = line.indexOf("=");
                    if (i < 1) continue;
                    const k = line.slice(0, i), v = line.slice(i + 1).trim();
                    if (k === "gpu") info.gpu.push(v); else info[k] = v;
                }
                page.info = info;
            }
        }
    }
    property var volumes: []
    Process {
        running: true
        command: ["df", "-P", "-B1", "/", "/home"]
        stdout: StdioCollector { onStreamFinished: page.volumes = D.parseDf(text) }
    }

    PageHeader { theme: page.theme; title: "About"; summary: page.info.os || "NexusOS" }
    SettingRow { theme: page.theme; label: "Computer name"; detail: page.info.host || "…"; highlighted: page.host.highlight === "name" }
    SettingRow { theme: page.theme; label: "Processor"; detail: (page.info.cpu || "Unknown") + (page.info.threads ? " · " + page.info.threads + " threads" : "") }
    SettingRow { theme: page.theme; label: "Memory"; detail: page.info.memory ? Math.round(Number(page.info.memory) / 1048576) + " GB" : "…" }
    SettingRow { theme: page.theme; label: "Graphics"; visible: (page.info.gpu || []).length > 0; detail: (page.info.gpu || []).join("\n") }
    SettingRow {
        theme: page.theme; label: "Storage"; highlighted: page.host.highlight === "storage"
        detail: D.disk(page.volumes)
        Button { theme: page.theme; label: "Manage storage"; onClicked: page.host.open("storage", "") }
    }
    SettingRow { theme: page.theme; label: "Kernel"; detail: page.info.kernel || "…" }
    SettingRow {
        theme: page.theme; label: "Welcome setup"; highlighted: page.host.highlight === "welcome"
        detail: "Choose what you use this computer for, and Nexus suggests apps and settings to go with it."
        Button { theme: page.theme; label: "Run again"; onClicked: page.host.openWelcome() }
    }
    SettingRow {
        theme: page.theme; label: "Bring files from Windows or macOS"; highlighted: page.host.highlight === "movein"
        detail: "Copy documents, photos, music and videos from an old drive. The old drive is only read, never changed."
        Button { theme: page.theme; label: "Open"; onClicked: page.host.moveIn() }
    }
    SettingRow {
        theme: page.theme; label: "Earlier versions"; highlighted: page.host.highlight === "generations"
        detail: "Every system change makes a new version. If an update breaks something, it goes back by itself; or restart, hold Space as the computer starts, and choose an earlier one."
    }
    // Start fresh: NexusOS's own settings back to how they came, files and apps kept (nexus-reset-settings).
    property string resetKept: ""
    property bool confirmReset: false
    Process { id: resetStatus; running: true; command: ["nexus-reset-settings", "status"]
              stdout: StdioCollector { onStreamFinished: page.resetKept = text.trim() } }
    SettingRow {
        theme: page.theme; label: "Start fresh"; highlighted: page.host.highlight === "reset"
        detail: page.confirmReset ? "Your desktop, language and Nexus settings go back to how they came, and the desktop restarts. Files and apps stay; the old settings are kept, so this can be undone."
              : page.resetKept ? "Settings were reset. Put back brings the earlier settings back."
              : "Settings back to how they came, keeping all your files and apps."
        Row {
            spacing: 8
            Button { visible: page.resetKept !== "" && !page.confirmReset; theme: page.theme; label: "Put back"
                     onClicked: Quickshell.execDetached(["nexus-reset-settings", "undo"]) }
            Button { visible: page.confirmReset; theme: page.theme; label: "Cancel"; onClicked: page.confirmReset = false }
            Button { theme: page.theme; primary: page.confirmReset; label: page.confirmReset ? "Reset settings" : "Start fresh…"
                     onClicked: { if (!page.confirmReset) { page.confirmReset = true; return } Quickshell.execDetached(["nexus-reset-settings"]) } }
        }
    }

    property var secureBoot: null
    property string secureBootMessage: ""
    Process { id: sbStatus; running: true; command: ["nexus-secure-boot"]
              stdout: StdioCollector { onStreamFinished: page.secureBoot = SB.parse(text) } }
    Process {
        id: sbEnroll
        command: [Quickshell.env("NEXUS_PKEXEC") || "/run/wrappers/bin/pkexec", "/run/current-system/sw/bin/nexus-secure-boot-enroll"]
        stdout: StdioCollector { id: sbOut }
        onExited: code => { let r = {}; try { r = JSON.parse(sbOut.text || "{}") } catch (e) {} page.secureBootMessage = r.error || r.message || (code ? "Not done." : ""); sbStatus.running = true }
    }
    SettingRow {
        theme: page.theme; label: "Secure Boot"; highlighted: page.host.highlight === "secureboot"
        detail: SB.step(page.secureBoot).detail + (page.secureBootMessage ? "\n" + page.secureBootMessage : "")
        Button { visible: SB.step(page.secureBoot).action === "propose"; theme: page.theme; label: "Turn on…"
                 onClicked: page.host.proposeSystem(SB.PLAN) }
        Button { visible: SB.step(page.secureBoot).action === "firmware"; theme: page.theme; label: "Restart into firmware settings"
                 onClicked: Quickshell.execDetached(["systemctl", "reboot", "--firmware-setup"]) }
        Button { visible: SB.step(page.secureBoot).action === "enroll"; theme: page.theme; label: sbEnroll.running ? "Working…" : "Put the keys in"
                 onClicked: if (!sbEnroll.running) sbEnroll.running = true }
    }
}
