import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/feedback.mjs" as F

// Settings, Feedback: tell the NexusOS project about a problem or an idea.
// Nothing is sent by itself. The report is built from what you write and
// what you tick, shown in full, and "Open on GitHub" opens it as a new issue
// in your browser, where you can still change it before submitting it.
// Recent crashes come from systemd-coredump (coredumpctl, as you): the
// program and how it stopped, never its memory or your files.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property string home: Quickshell.env("HOME") || ""
    property var crashes: []
    property var system: ({})
    property bool includeSystem: true
    property string crashId: ""
    readonly property var crash: crashes.find(c => c.id === crashId) || null
    readonly property var built: F.report({ text: description.text, crash: crash, system: includeSystem ? system : null })

    Process { running: true; command: ["coredumpctl", "--json=short", "--no-pager", "list", "--since=-14d"]
              stdout: StdioCollector { onStreamFinished: page.crashes = F.crashes(text, page.home).slice(0, 5) } }
    Process {
        running: true
        command: ["sh", "-c", ". /etc/os-release 2>/dev/null; echo \"NexusOS=$PRETTY_NAME\"; echo \"Kernel=$(uname -r)\"; " +
            "echo \"Processor=$(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | sed 's/^ *//')\"; " +
            "echo \"Memory=$(( $(grep MemTotal /proc/meminfo | tr -s ' ' | cut -d' ' -f2) / 1048576 + 1 )) GB\"; " +
            "echo \"Graphics=$(lspci 2>/dev/null | grep -Ei 'vga|3d|display' | cut -d: -f3- | sed 's/^ *//' | paste -sd ';' -)\""]
        stdout: StdioCollector {
            onStreamFinished: {
                const s = {};
                for (const line of text.split("\n")) { const i = line.indexOf("="); if (i > 0) s[line.slice(0, i)] = line.slice(i + 1).trim() }
                page.system = s;
            }
        }
    }

    PageHeader { theme: page.theme; title: "Feedback"; summary: "Tell the NexusOS project about a problem or an idea. Nothing is sent unless you send it." }

    SettingRow {
        theme: page.theme; label: "Crash notices"; highlighted: page.host.highlight === "crashnotices"
        detail: "Say when an app crashes, so you can report it. The notice stays on this computer."
        Switch { theme: page.theme; name: "Crash notices"; checked: page.host.desktop.crashNotices
                 onToggled: page.host.desktop.set("crashNotices", !page.host.desktop.crashNotices) }
    }

    SettingRow {
        theme: page.theme; label: "Report a problem or an idea"; highlighted: page.host.highlight === "report"
        detail: "What happened, or what you would like. Write it as you would tell a friend."
    }
    Rectangle {
        width: page.width; height: 120; radius: 10
        color: page.theme.chip
        border.color: description.activeFocus ? page.theme.focus : page.theme.glassBorder
        TextEdit {
            id: description
            anchors { fill: parent; margins: 12 }
            wrapMode: TextEdit.Wrap; color: page.theme.ink; font.pixelSize: 14; textFormat: TextEdit.PlainText
            activeFocusOnTab: true
            Accessible.role: Accessible.EditableText
            Accessible.name: "What happened"
        }
    }
    SettingRow {
        theme: page.theme; label: "Include this computer's details"
        detail: [page.system.NexusOS, page.system.Processor, page.system.Memory].filter(Boolean).join(" · ") || "…"
        Switch { theme: page.theme; name: "Include this computer's details"; checked: page.includeSystem
                 onToggled: page.includeSystem = !page.includeSystem }
    }
    SettingRow {
        visible: page.crashes.length > 0
        theme: page.theme; label: "Include a crash"
        detail: "The program and how it stopped; never its memory or your files."
    }
    Flow {
        width: page.width; spacing: 6
        Repeater {
            model: page.crashes
            delegate: Button {
                required property var modelData
                theme: page.theme; primary: page.crashId === modelData.id
                label: modelData.name + " · " + new Date(modelData.when).toLocaleString(Qt.locale(), Locale.ShortFormat)
                onClicked: page.crashId = page.crashId === modelData.id ? "" : modelData.id
            }
        }
    }

    SettingRow { theme: page.theme; label: "The report"; detail: "Exactly what is sent, and to github.com/TheMatthewWalker/NexusOS, in public." }
    Rectangle {
        width: page.width; height: preview.implicitHeight + 24; radius: 10
        color: "transparent"; border.color: page.theme.glassBorder
        Text {
            id: preview
            x: 12; y: 12; width: parent.width - 24; wrapMode: Text.Wrap; textFormat: Text.PlainText
            text: page.built.title + "\n\n" + page.built.body; color: page.theme.inkSecondary; font.pixelSize: 12
        }
    }
    Row {
        spacing: 8
        Button {
            theme: page.theme; primary: true; label: "Open on GitHub"
            enabled: description.text.trim() !== "" || !!page.crash
            onClicked: Quickshell.execDetached(["xdg-open", F.issueUrl(page.built.title, page.built.body)])
        }
        Button { theme: page.theme; label: "Copy the report"
                 onClicked: Quickshell.execDetached(["wl-copy", page.built.title + "\n\n" + page.built.body]) }
    }
}
