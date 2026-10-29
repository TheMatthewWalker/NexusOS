import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/describe.mjs" as D

// Settings, About: this computer and its storage. Read-only.
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
        Button { theme: page.theme; visible: D.diskLow(page.volumes); label: "Show biggest files"; onClicked: page.host.openFolder(Quickshell.env("HOME")) }
    }
    SettingRow { theme: page.theme; label: "Kernel"; detail: page.info.kernel || "…" }
    SettingRow {
        theme: page.theme; label: "Earlier versions"; highlighted: page.host.highlight === "generations"
        detail: "Every system change makes a new version. If an update breaks something, restart and choose an earlier one in the boot menu."
    }
}
