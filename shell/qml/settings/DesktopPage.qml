import QtQuick

// Settings, Desktop: how windows behave, and the desktop's effects.
Column {
    id: page
    property var theme
    property var host
    readonly property var desktop: host.desktop
    width: parent ? parent.width : 600
    spacing: 10

    PageHeader { theme: page.theme; title: "Desktop"; summary: "How windows open and move, and the desktop's effects." }

    SettingRow {
        theme: page.theme; label: "Windows"; highlighted: page.host.highlight === "windows"
        detail: page.desktop.windows === "floating"
            ? "Windows float and overlap, as on Windows or macOS. Drag the title bar to move one; Super+T tiles a single window."
            : "Windows tile side by side and fill the screen. Super+T floats a single window."
        Choice { theme: page.theme; name: "Windows"; current: page.desktop.windows
                 options: [{ value: "floating", label: "Floating" }, { value: "tiling", label: "Tiled" }]
                 onChose: v => page.desktop.set("windows", v) }
    }
    SettingRow {
        theme: page.theme; label: "Apps light or dark"; highlighted: page.host.highlight === "apptheme"
        detail: "The look of other apps' windows, such as Files, Text Editor and VLC. Apps already open change when they next start."
        Choice { theme: page.theme; name: "Apps"; current: page.desktop.appTheme
                 options: [{ value: "dark", label: "Dark" }, { value: "light", label: "Light" }]
                 onChose: v => page.desktop.set("appTheme", v) }
    }
    SettingRow {
        theme: page.theme; label: "Blur"; highlighted: page.host.highlight === "blur"
        detail: "Frosted glass behind windows and panels. Off saves a little graphics power."
        Switch { theme: page.theme; name: "Blur"; checked: page.desktop.blur; onToggled: page.desktop.set("blur", !page.desktop.blur) }
    }
    SettingRow {
        theme: page.theme; label: "Animations"; highlighted: page.host.highlight === "animations"
        detail: "Windows slide and fade as they open and close."
        Switch { theme: page.theme; name: "Animations"; checked: page.desktop.animations
                 onToggled: page.desktop.set("animations", !page.desktop.animations) }
    }
    SettingRow {
        theme: page.theme; label: "Do not disturb"; highlighted: page.host.highlight === "donotdisturb"
        detail: page.desktop.doNotDisturb
            ? "Notifications wait quietly behind the bell in the top bar; only urgent ones pop up."
            : "Notifications from apps pop up in the top right, and wait behind the bell in the top bar."
        Switch { theme: page.theme; name: "Do not disturb"; checked: page.desktop.doNotDisturb
                 onToggled: page.desktop.set("doNotDisturb", !page.desktop.doNotDisturb) }
    }
    SettingRow {
        theme: page.theme; label: "Keyboard shortcuts"; highlighted: page.host.highlight === "shortcuts"
        detail: "Super+Space Nexus · Super+Return terminal · Super+E files · Super+I Settings · Super+Q close · " +
                "Super+M maximise · Super+F fullscreen · Super+1–5 workspaces · Super+L lock"
    }
}
