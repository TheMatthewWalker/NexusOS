import QtQuick
import Quickshell
import Quickshell.Services.UPower
import "../lib/describe.mjs" as D
import "../lib/power.mjs" as P

// Settings, Power: the battery, the power mode (power-profiles-daemon), when
// the screen turns off and the computer sleeps, what closing the lid does
// (Idle.qml carries these out), and lock, log out, restart and shut down.
// Restart and shut down ask once more.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property var battery: UPower.displayDevice
    readonly property bool hasBattery: !!(battery && battery.isLaptopBattery && battery.isPresent)
    readonly property string batteryState: !hasBattery ? "unknown" : ({
        [UPowerDeviceState.Charging]: "charging", [UPowerDeviceState.Discharging]: "discharging",
        [UPowerDeviceState.FullyCharged]: "full", [UPowerDeviceState.Empty]: "empty",
        [UPowerDeviceState.PendingCharge]: "pending-charge", [UPowerDeviceState.PendingDischarge]: "pending-discharge"
    })[battery.state] || "unknown"
    property string confirm: ""        // "reboot" or "poweroff" while asking
    readonly property var desktop: host.desktop

    PageHeader { theme: page.theme; title: "Power"; summary: "Battery, power mode, sleep and the lid, and turning the computer off." }

    SettingRow {
        theme: page.theme; label: "Battery"; highlighted: page.host.highlight === "battery"
        detail: D.power({ available: !!(page.battery && page.battery.ready), present: page.hasBattery, state: page.batteryState,
                          percentage: page.hasBattery ? page.battery.percentage : NaN,
                          timeToEmpty: page.hasBattery ? page.battery.timeToEmpty : 0,
                          timeToFull: page.hasBattery ? page.battery.timeToFull : 0 })
    }
    SettingRow {
        theme: page.theme; label: "Power mode"; highlighted: page.host.highlight === "powermode"
        detail: PowerProfiles.degradationReason !== PerformanceDegradationReason.None
                ? "Performance is limited right now (the computer is hot, or on a low battery)."
                : "Balanced suits most things. Power saver makes the battery last longer."
        Choice {
            theme: page.theme; name: "Power mode"; current: PowerProfiles.profile
            options: [{ value: PowerProfile.PowerSaver, label: "Power saver" }, { value: PowerProfile.Balanced, label: "Balanced" }]
                .concat(PowerProfiles.hasPerformanceProfile ? [{ value: PowerProfile.Performance, label: "Performance" }] : [])
            onChose: v => PowerProfiles.profile = v
        }
    }
    SettingRow {
        theme: page.theme; label: "Screen off"; highlighted: page.host.highlight === "screenoff"
        detail: "Turns the screen off when nothing has been touched for a while. Videos and calls keep it on."
        Choice { theme: page.theme; name: "Screen off after"; current: page.desktop.screenOffMinutes
                 options: P.options(P.SCREEN_OFF); onChose: v => page.desktop.set("screenOffMinutes", v) }
    }
    SettingRow {
        theme: page.theme; label: "Sleep"; highlighted: page.host.highlight === "sleep"
        detail: P.sleepDetail(page.desktop.sleepBatteryMinutes, page.desktop.sleepPluggedMinutes, UPower.onBattery, page.hasBattery)
        Column {
            spacing: 6
            Row {
                visible: page.hasBattery; spacing: 8
                Text { width: 90; anchors.verticalCenter: parent.verticalCenter; text: "On battery"; color: page.theme.inkSecondary; font.pixelSize: 12 }
                Choice { theme: page.theme; name: "Sleep on battery after"; current: page.desktop.sleepBatteryMinutes
                         options: P.options(P.SLEEP); onChose: v => page.desktop.set("sleepBatteryMinutes", v) }
            }
            Row {
                spacing: 8
                Text { width: 90; anchors.verticalCenter: parent.verticalCenter; text: page.hasBattery ? "Plugged in" : "After"
                       color: page.theme.inkSecondary; font.pixelSize: 12 }
                Choice { theme: page.theme; name: "Sleep plugged in after"; current: page.desktop.sleepPluggedMinutes
                         options: P.options(P.SLEEP); onChose: v => page.desktop.set("sleepPluggedMinutes", v) }
            }
        }
    }
    SettingRow {
        visible: page.hasBattery
        theme: page.theme; label: "Closing the lid"; highlighted: page.host.highlight === "lid"
        detail: page.desktop.lidAction === "nothing"
            ? "Nothing happens: useful with a screen, keyboard and mouse plugged in. It keeps running in a bag too, so take care."
            : page.desktop.lidAction === "lock" ? "The screen locks and the computer keeps running."
            : "The computer sleeps, and the screen is locked when you open it."
        Choice { theme: page.theme; name: "When the lid closes"; current: page.desktop.lidAction
                 options: P.LID.map(a => ({ value: a, label: P.lidLabel(a) })); onChose: v => page.desktop.set("lidAction", v) }
    }
    SettingRow {
        theme: page.theme; label: "Session"; highlighted: page.host.highlight === "session"
        detail: page.confirm === "reboot" ? "Restart now? Unsaved work in open apps is lost."
              : page.confirm === "poweroff" ? "Shut down now? Unsaved work in open apps is lost."
              : "Lock keeps your apps open; log out closes them."
        Row {
            spacing: 8
            Button { theme: page.theme; visible: page.confirm === ""; label: "Lock"; onClicked: page.host.lock() }
            Button { theme: page.theme; visible: page.confirm === ""; label: "Log out"; onClicked: page.host.hypr(["dispatch", "exit"]) }
            Button { theme: page.theme; visible: page.confirm === ""; label: "Sleep"; onClicked: Quickshell.execDetached(["systemctl", "suspend"]) }
            Button { theme: page.theme; visible: page.confirm === ""; label: "Restart"; onClicked: page.confirm = "reboot" }
            Button { theme: page.theme; visible: page.confirm === ""; danger: true; label: "Shut down"; onClicked: page.confirm = "poweroff" }
            Button { theme: page.theme; visible: page.confirm !== ""; label: "Cancel"; onClicked: page.confirm = "" }
            Button { theme: page.theme; visible: page.confirm !== ""; primary: true; danger: true
                     label: page.confirm === "reboot" ? "Restart" : "Shut down"
                     onClicked: Quickshell.execDetached(["systemctl", page.confirm]) }
        }
    }
}
