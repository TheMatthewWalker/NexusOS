import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Services.UPower
import "lib/power.mjs" as P

// Sleep, the screen and the lid, from Settings, Power (DesktopSettings):
//  - the screen turns off after a while with nothing touched;
//  - the computer sleeps after a while (on battery and plugged in separately);
//  - closing the lid sleeps, only locks, or does nothing;
//  - the screen is locked before every sleep, however it starts (idle, the
//    lid, the Power page, a key), so nothing shows when the lid opens.
// Video calls and films that ask the desktop to stay awake keep it awake.
Scope {
    id: idle
    property var desktop
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    readonly property string lockCommand: Quickshell.env("NEXUS_LOCK") || "hyprlock"
    readonly property bool onBattery: UPower.onBattery
    readonly property int screenOffMinutes: desktop ? desktop.screenOffMinutes : 5
    readonly property int sleepMinutes: !desktop ? 0 : onBattery ? desktop.sleepBatteryMinutes : desktop.sleepPluggedMinutes
    readonly property string lidAction: desktop ? desktop.lidAction : "sleep"

    function lock() { Quickshell.execDetached(lockCommand.split(" ")) }

    IdleMonitor {
        enabled: idle.screenOffMinutes > 0
        timeout: idle.screenOffMinutes * 60
        respectInhibitors: true
        onIsIdleChanged: Quickshell.execDetached([idle.hyprctl, "dispatch", "dpms", isIdle ? "off" : "on"])
    }
    IdleMonitor {
        enabled: idle.sleepMinutes > 0
        timeout: idle.sleepMinutes * 60
        respectInhibitors: true
        onIsIdleChanged: if (isIdle) Quickshell.execDetached(["systemctl", "suspend"])
    }

    // Lock before sleep. A "delay" inhibitor makes logind wait (a few seconds at
    // most) when a sleep starts; the lock screen comes up, then the inhibitor
    // goes so the computer can sleep, and it is taken again on waking.
    Process {
        id: delay
        running: true
        command: ["systemd-inhibit", "--what=sleep", "--mode=delay", "--who=NexusOS", "--why=Lock the screen first", "sleep", "infinity"]
    }
    Process {
        id: watch
        running: true
        // gdbus subscribes to logind's signals as any user may (busctl monitor needs root).
        command: ["gdbus", "monitor", "--system", "--dest", "org.freedesktop.login1", "--object-path", "/org/freedesktop/login1"]
        stdout: SplitParser {
            onRead: line => {
                const going = P.sleepSignal(line);
                if (going === true) {
                    idle.lock();
                    release.restart();          // a moment for the lock screen to cover the desktop
                } else if (going === false) {
                    delay.running = true;       // awake again: wait for the next sleep
                }
            }
        }
    }
    Timer { id: release; interval: 700; onTriggered: delay.running = false }

    // The lid. Sleep is logind's own (it also covers the login screen). For
    // "lock" or "nothing" the shell holds a lid-switch inhibitor while that is
    // the choice, and for "lock" Hyprland's lid switch locks the screen.
    Process {
        running: idle.lidAction !== "sleep"
        command: ["systemd-inhibit", "--what=handle-lid-switch", "--mode=block", "--who=NexusOS",
                  "--why=Settings, Power: the lid does not sleep", "sleep", "infinity"]
    }
    onLidActionChanged: applyLid()
    Component.onCompleted: applyLid()
    function applyLid() {
        Quickshell.execDetached([hyprctl, "keyword", "unbind", ",switch:on:Lid Switch"]);
        if (lidAction === "lock") Quickshell.execDetached([hyprctl, "keyword", "bindl", ",switch:on:Lid Switch,exec," + lockCommand]);
    }
}
