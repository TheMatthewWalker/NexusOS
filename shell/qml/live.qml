// The NexusOS installer's live session (nix/installer/iso.nix). The live
// system runs from the USB stick and keeps nothing, so it offers only what
// installing needs: connecting to Wi-Fi and starting the installer. No
// Nexus, apps, Welcome setup or system changes: those belong to the
// installed system, which has its own Core, Guardian and accounts.
import QtQuick
import Quickshell
import Quickshell.Wayland
import "settings"

ShellRoot {
    id: root
    readonly property var theme: ({
        ink: "#E3EAFF", inkSecondary: "#9AA9C9", glass: Qt.rgba(0.04, 0.07, 0.15, 0.80),
        glassBorder: "#2A3A66", focus: "#86A8FF", primary: "#7C6BFF", chip: "#121B33",
        orbit: Qt.rgba(0.55, 0.62, 1.0, 0.22), error: "#FF8FA3", muted: "#7885A3"
    })
    readonly property string terminal: Quickshell.env("NEXUS_TERMINAL") || "kitty"
    function install() {
        Quickshell.execDetached([Quickshell.env("NEXUS_HYPRCTL") || "hyprctl", "dispatch", "focuswindow", "title:^(Install NexusOS)$"]);
        Quickshell.execDetached(["sh", "-c", "pgrep -f nexus-install-welcome > /dev/null || exec \"$1\" --title 'Install NexusOS' nexus-install-welcome",
                                 "sh", root.terminal]);
    }

    DesktopSettings { id: desktop }
    // Only the Network page: Wi-Fi is the one setting installing can use.
    Settings { id: settings; theme: root.theme; bridge: null; desktop: desktop; only: ["network"] }
    TopBar { theme: root.theme; settings: settings; live: true }
    Wallpaper {}

    // The welcome card, under any window (the installer's terminal, Settings).
    Variants {
        model: Quickshell.screens
        PanelWindow {
            required property var modelData
            screen: modelData
            anchors { top: true; bottom: true; left: true; right: true }
            exclusionMode: ExclusionMode.Ignore
            WlrLayershell.layer: WlrLayer.Bottom
            WlrLayershell.namespace: "nexus-live"
            color: "transparent"

            Rectangle {
                anchors.centerIn: parent
                width: Math.min(620, parent.width - 48); height: card.implicitHeight + 64
                radius: 24; color: root.theme.glass; border.color: root.theme.glassBorder
                Column {
                    id: card
                    x: 32; y: 32; width: parent.width - 64; spacing: 16
                    Text { text: "Install NexusOS"; color: root.theme.ink; font.pixelSize: 30; font.weight: 600 }
                    Text {
                        width: parent.width; wrapMode: Text.Wrap; color: root.theme.inkSecondary; font.pixelSize: 15; lineHeight: 1.3
                        text: "This is the installer, running from the USB stick. Nothing here is kept: Nexus, your apps and " +
                              "the Welcome setup start once NexusOS is installed on this computer.\n\n" +
                              "No internet connection is needed; everything is on the stick."
                    }
                    Text {
                        color: root.theme.inkSecondary; font.pixelSize: 13
                        text: settings.wifi.ssid ? "Connected to " + settings.wifi.ssid
                            : settings.wifi.wired ? "Connected by cable" : "Not connected (that is fine)"
                    }
                    Row {
                        spacing: 10
                        Button { theme: root.theme; primary: true; label: "Install NexusOS"; onClicked: root.install() }
                        Button { theme: root.theme; label: "Connect to Wi-Fi"; onClicked: settings.open("network", "wifi") }
                    }
                    Row {
                        spacing: 10
                        Button { theme: root.theme; label: "Restart"; onClicked: Quickshell.execDetached(["systemctl", "reboot"]) }
                        Button { theme: root.theme; label: "Shut down"; onClicked: Quickshell.execDetached(["systemctl", "poweroff"]) }
                    }
                }
            }
        }
    }
}
