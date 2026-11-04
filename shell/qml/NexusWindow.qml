import QtQuick
import Quickshell

// Every NexusOS app window (Settings, Apps, Code, ...). When the person
// closes it from its title bar or with Super+Q, Hyprland destroys the window
// but Quickshell's `visible` stays true, so setting it true again would show
// nothing until the shell restarted. Marking it hidden when it is closed lets
// it open again.
FloatingWindow {
    onClosed: visible = false
    onBackingWindowVisibleChanged: if (!backingWindowVisible && visible) visible = false
}
