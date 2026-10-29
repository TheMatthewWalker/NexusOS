import QtQuick
import Quickshell
import Quickshell.Services.UPower
import "../lib/describe.mjs" as D

// Settings, Power: the battery, the power mode (power-profiles-daemon) and
// lock, log out, restart and shut down. Restart and shut down ask once more.
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

    PageHeader { theme: page.theme; title: "Power"; summary: "Battery, power mode, and turning the computer off." }

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
        theme: page.theme; label: "Session"; highlighted: page.host.highlight === "session"
        detail: page.confirm === "reboot" ? "Restart now? Unsaved work in open apps is lost."
              : page.confirm === "poweroff" ? "Shut down now? Unsaved work in open apps is lost."
              : "Lock keeps your apps open; log out closes them."
        Row {
            spacing: 8
            Button { theme: page.theme; visible: page.confirm === ""; label: "Lock"; onClicked: page.host.lock() }
            Button { theme: page.theme; visible: page.confirm === ""; label: "Log out"; onClicked: page.host.hypr(["dispatch", "exit"]) }
            Button { theme: page.theme; visible: page.confirm === ""; label: "Restart"; onClicked: page.confirm = "reboot" }
            Button { theme: page.theme; visible: page.confirm === ""; danger: true; label: "Shut down"; onClicked: page.confirm = "poweroff" }
            Button { theme: page.theme; visible: page.confirm !== ""; label: "Cancel"; onClicked: page.confirm = "" }
            Button { theme: page.theme; visible: page.confirm !== ""; primary: true; danger: true
                     label: page.confirm === "reboot" ? "Restart" : "Shut down"
                     onClicked: Quickshell.execDetached(["systemctl", page.confirm]) }
        }
    }
}
