import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Hyprland
import Quickshell.Services.Pipewire
import Quickshell.Services.UPower
import Quickshell.Services.SystemTray
import Quickshell.Services.Mpris
import Quickshell.Networking
import "lib/privacy.mjs" as Priv
import "lib/calendar.mjs" as Cal

// The top bar (design: "Desktop B · Floating glass with top bar"): the Nexus
// orb, workspaces, the date and time, and Wi-Fi, sound and battery. Each
// status opens its page in Settings. Windows keep clear of it.
PanelWindow {
    id: bar
    property var theme
    property var settings
    property var guardian: null      // Guardian.qml: a system change waiting, under way, or needing a restart
    property var notifications: null // Notifications.qml: the bell opens the centre
    property bool live: false        // the installer's live session (live.qml): no Nexus, no apps
    signal openNexus()
    signal openApps()

    anchors { top: true; left: true; right: true }
    margins { top: 10; left: 12; right: 12 }
    implicitHeight: 40
    color: "transparent"
    WlrLayershell.layer: WlrLayer.Top
    WlrLayershell.namespace: "nexus-top-bar"

    SystemClock { id: clock; precision: SystemClock.Minutes }
    readonly property var sink: Pipewire.defaultAudioSink
    PwObjectTracker { objects: bar.sink ? [bar.sink] : [] }
    readonly property bool hasAudio: !!(sink && sink.audio)
    readonly property var battery: UPower.displayDevice
    readonly property bool hasBattery: !!(battery && battery.isLaptopBattery && battery.isPresent)
    readonly property var wifi: settings.wifi
    readonly property int activeWorkspace: Hyprland.focusedWorkspace ? Hyprland.focusedWorkspace.id : 1
    // Workspaces 1-5 always; a higher one shows while it has windows.
    readonly property var workspaces: {
        const ids = [1, 2, 3, 4, 5];
        for (const w of Hyprland.workspaces.values) if (w.id > 5 && !ids.includes(w.id)) ids.push(w.id);
        return ids.sort((a, b) => a - b);
    }
    function occupied(id) { return Hyprland.workspaces.values.some(w => w.id === id && w.toplevels.values.length > 0) }

    Rectangle {
        anchors.fill: parent
        radius: 14
        color: Qt.rgba(0.04, 0.07, 0.15, 0.86)
        border.color: bar.theme.glassBorder

        Row {
            x: 10; anchors.verticalCenter: parent.verticalCenter
            spacing: 10
            // The Nexus orb.
            Rectangle {
                visible: !bar.live
                width: 24; height: 24; radius: 12; anchors.verticalCenter: parent.verticalCenter
                gradient: Gradient {
                    GradientStop { position: 0; color: "#FFE3B8" }
                    GradientStop { position: 0.4; color: "#FFA65C" }
                    GradientStop { position: 1; color: "#8C50C8" }
                }
                border.color: orbHover.hovered ? bar.theme.focus : "transparent"
                HoverHandler { id: orbHover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: bar.openNexus() }
                Accessible.role: Accessible.Button
                Accessible.name: "Open Nexus"
            }
            // All apps.
            Rectangle {
                visible: !bar.live
                width: 28; height: 26; radius: 8; anchors.verticalCenter: parent.verticalCenter
                color: appsHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.2) : "transparent"
                Image { anchors.centerIn: parent; width: 16; height: 16; sourceSize: Qt.size(32, 32); source: "icons/apps.svg" }
                HoverHandler { id: appsHover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: bar.openApps() }
                Accessible.role: Accessible.Button
                Accessible.name: "All apps"
            }
            Row {
                spacing: 4; anchors.verticalCenter: parent.verticalCenter
                Repeater {
                    model: bar.workspaces
                    delegate: Rectangle {
                        required property int modelData
                        readonly property bool active: modelData === bar.activeWorkspace
                        width: Math.max(28, wsLabel.implicitWidth + 16); height: 26; radius: 8
                        color: active ? bar.theme.primary : wsHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.2) : "transparent"
                        Text {
                            id: wsLabel; anchors.centerIn: parent
                            text: modelData
                            color: active ? "#FFFFFF" : bar.occupied(modelData) ? bar.theme.ink : bar.theme.inkSecondary
                            font.pixelSize: 13; font.weight: active ? 600 : 400
                        }
                        // A dot under a workspace that has windows.
                        Rectangle { visible: !active && bar.occupied(modelData); width: 4; height: 4; radius: 2
                                    anchors { horizontalCenter: parent.horizontalCenter; bottom: parent.bottom; bottomMargin: 2 }
                                    color: bar.theme.focus }
                        HoverHandler { id: wsHover; cursorShape: Qt.PointingHandCursor }
                        TapHandler { onTapped: Hyprland.dispatch("workspace " + modelData) }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Workspace " + modelData + (active ? ", current" : "")
                    }
                }
            }
        }

        Text {
            anchors.centerIn: parent
            text: Qt.formatDateTime(clock.date, "ddd d MMM · HH:mm")
            color: bar.theme.ink; font.pixelSize: 13; font.weight: 500
            HoverHandler { cursorShape: Qt.PointingHandCursor }
            TapHandler { onTapped: bar.toggleCalendar() }
            Accessible.role: Accessible.Button
            Accessible.name: Qt.formatDateTime(clock.date, "dddd d MMMM yyyy, HH:mm") + ". Opens the calendar."
        }

        Row {
            anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
            spacing: 14
            component Status: Row {
                id: st
                property string icon
                property string label: ""
                property string name
                property string page
                property real dim: 1
                spacing: 6
                Image { width: 16; height: 16; sourceSize: Qt.size(32, 32); source: "icons/" + st.icon + ".svg"
                        opacity: st.dim; anchors.verticalCenter: parent.verticalCenter }
                Text { visible: st.label !== ""; text: st.label; color: bar.theme.inkSecondary; font.pixelSize: 12
                       anchors.verticalCenter: parent.verticalCenter }
                HoverHandler { cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: bar.settings.open(st.page, "") }
                Accessible.role: Accessible.Button
                Accessible.name: st.name
            }
            // Microphone, camera and screen sharing in use: always shown, never hidden.
            Row {
                id: privacy
                readonly property var streams: Pipewire.nodes.values.filter(n => n.isStream)
                readonly property var use: Priv.inUse(privacy.streams, "pw-record")
                readonly property string text: Priv.describe(use)
                PwObjectTracker { objects: privacy.streams }
                visible: text !== ""; spacing: 6
                Rectangle { width: 8; height: 8; radius: 4; color: "#FF8FA3"; anchors.verticalCenter: parent.verticalCenter }
                Image { visible: privacy.use.microphone.length > 0; width: 16; height: 16; sourceSize: Qt.size(32, 32)
                        source: "icons/mic.svg"; anchors.verticalCenter: parent.verticalCenter }
                Image { visible: privacy.use.camera.length > 0 || privacy.use.screen; width: 16; height: 16; sourceSize: Qt.size(32, 32)
                        source: "icons/display.svg"; anchors.verticalCenter: parent.verticalCenter }
                Text { text: privacy.use.screen ? "Sharing screen" : (privacy.use.microphone.concat(privacy.use.camera)[0] || "")
                       color: bar.theme.ink; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter; textFormat: Text.PlainText
                       width: Math.min(implicitWidth, 140); elide: Text.ElideRight }
                Accessible.role: Accessible.StaticText
                Accessible.name: privacy.text
            }
            // Now playing, from any app that says what it plays (MPRIS): play,
            // pause and skip without finding its window.
            Row {
                id: media
                readonly property var player: Priv.pick(Mpris.players.values)
                visible: !!player && Priv.nowPlaying(player) !== ""
                spacing: 6
                Text { text: "⏮"; visible: !!media.player && media.player.canGoPrevious; color: bar.theme.inkSecondary; font.pixelSize: 12
                       anchors.verticalCenter: parent.verticalCenter
                       TapHandler { onTapped: media.player.previous() } Accessible.role: Accessible.Button; Accessible.name: "Previous" }
                Text { text: media.player && media.player.isPlaying ? "⏸" : "▶"; color: bar.theme.ink; font.pixelSize: 12
                       anchors.verticalCenter: parent.verticalCenter
                       TapHandler { onTapped: if (media.player.canTogglePlaying) media.player.togglePlaying() }
                       Accessible.role: Accessible.Button; Accessible.name: media.player && media.player.isPlaying ? "Pause" : "Play" }
                Text { text: "⏭"; visible: !!media.player && media.player.canGoNext; color: bar.theme.inkSecondary; font.pixelSize: 12
                       anchors.verticalCenter: parent.verticalCenter
                       TapHandler { onTapped: media.player.next() } Accessible.role: Accessible.Button; Accessible.name: "Next" }
                Text { width: Math.min(implicitWidth, 200); elide: Text.ElideRight; textFormat: Text.PlainText
                       text: Priv.nowPlaying(media.player); color: bar.theme.inkSecondary; font.pixelSize: 12
                       anchors.verticalCenter: parent.verticalCenter
                       TapHandler { onTapped: if (media.player.canRaise) media.player.raise() } }
            }
            // The system tray: apps that keep an icon while running (Steam,
            // Discord, VPN and sync clients). Click: the app; right-click: its menu.
            Row {
                spacing: 8
                Repeater {
                    model: SystemTray.items
                    delegate: Image {
                        id: trayIcon
                        required property var modelData
                        width: 16; height: 16; sourceSize: Qt.size(32, 32)
                        source: modelData.icon; anchors.verticalCenter: parent ? parent.verticalCenter : undefined
                        HoverHandler { cursorShape: Qt.PointingHandCursor }
                        TapHandler {
                            acceptedButtons: Qt.LeftButton | Qt.RightButton
                            onTapped: (point, button) => {
                                const item = trayIcon.modelData;
                                if (button === Qt.RightButton || item.onlyMenu) {
                                    if (item.hasMenu) {
                                        const p = trayIcon.mapToItem(null, 0, trayIcon.height + 6);
                                        item.display(bar, p.x, p.y);
                                    }
                                } else item.activate();
                            }
                        }
                        Accessible.role: Accessible.Button
                        Accessible.name: modelData.tooltipTitle || modelData.title || modelData.id
                    }
                }
            }
            Status {
                icon: "network"; page: "network"; dim: bar.wifi.ssid || bar.wifi.wired ? 1 : 0.4
                name: bar.wifi.ssid ? "Wi-Fi: " + bar.wifi.ssid : bar.wifi.wired ? "Wired" : "Not connected"
            }
            Status {
                icon: "sound"; page: "sound"; dim: bar.hasAudio && !bar.sink.audio.muted ? 1 : 0.4
                label: bar.hasAudio ? (bar.sink.audio.muted ? "Muted" : Math.round(bar.sink.audio.volume * 100) + "%") : ""
                name: "Sound: " + (bar.hasAudio ? label : "no output device")
            }
            Status {
                visible: bar.hasBattery
                icon: "battery"; page: "power"
                label: bar.hasBattery ? Math.round(bar.battery.percentage * 100) + "%" : ""
                name: "Battery " + label
            }
            // Guardian: shown only while a system change needs the person or is under way.
            Row {
                readonly property var g: bar.guardian
                readonly property string label: !g ? "" : g.updatesReady ? "Security updates are ready" : g.attention ? "Change waiting" : g.busy ? "Checking a change…"
                                              : g.restartNeeded ? "Restart to finish" : ""
                visible: label !== ""; spacing: 6
                Image { width: 16; height: 16; sourceSize: Qt.size(32, 32); source: "icons/guardian.svg"; anchors.verticalCenter: parent.verticalCenter }
                Text { text: parent.label; color: parent.g && parent.g.attention ? bar.theme.ink : bar.theme.inkSecondary; font.pixelSize: 12
                       anchors.verticalCenter: parent.verticalCenter }
                HoverHandler { cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: bar.guardian.open() }
                Accessible.role: Accessible.Button
                Accessible.name: "System change: " + label
            }
            // Notifications: the bell, with how many are waiting; it opens the centre.
            Row {
                readonly property var n: bar.notifications
                visible: !!n
                spacing: 6
                Image { width: 16; height: 16; sourceSize: Qt.size(32, 32); source: "icons/bell.svg"; anchors.verticalCenter: parent.verticalCenter
                        opacity: parent.n && (parent.n.count > 0 || parent.n.centreOpen) ? 1 : 0.5 }
                Text { visible: !!parent.n && parent.n.count > 0; text: parent.n ? String(parent.n.count) : ""
                       color: bar.theme.ink; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter }
                HoverHandler { cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: bar.notifications.centreOpen = !bar.notifications.centreOpen }
                Accessible.role: Accessible.Button
                Accessible.name: !parent.n ? "" : parent.n.count === 0 ? "Notifications: none" : "Notifications: " + parent.n.count
            }
            Status { icon: "settings"; page: ""; name: "Settings" }
        }
    }

    // The month calendar under the clock, as on Windows: today marked, months
    // either side, week numbers. Esc or the clock closes it.
    property bool calendarOpen: false
    property int calYear: clock.date.getFullYear()
    property int calMonth: clock.date.getMonth()
    function toggleCalendar() {
        if (!calendarOpen) { calYear = clock.date.getFullYear(); calMonth = clock.date.getMonth() }
        calendarOpen = !calendarOpen;
    }
    IpcHandler {
        target: "calendar"
        function toggle(): void { bar.toggleCalendar() }
        function state(): string { return JSON.stringify({ open: bar.calendarOpen, year: bar.calYear, month: bar.calMonth }) }
        function move(by: int): void { bar.moveMonth(by) }
    }
    function moveMonth(by) { const m = Cal.shift(calYear, calMonth, by); calYear = m.year; calMonth = m.month }
    readonly property int firstDay: Qt.locale().firstDayOfWeek % 7
    PanelWindow {
        visible: bar.calendarOpen && !bar.live
        anchors.top: true
        margins.top: 56
        implicitWidth: 320; implicitHeight: 330
        color: "transparent"
        exclusiveZone: 0
        WlrLayershell.namespace: "nexus-calendar"
        WlrLayershell.layer: WlrLayer.Top
        WlrLayershell.keyboardFocus: WlrKeyboardFocus.OnDemand
        Rectangle {
            anchors.fill: parent
            radius: 18
            color: bar.theme.glass
            border.color: bar.theme.glassBorder
            focus: true
            Keys.onEscapePressed: bar.calendarOpen = false
            Keys.onLeftPressed: bar.moveMonth(-1)
            Keys.onRightPressed: bar.moveMonth(1)
            Column {
                anchors { fill: parent; margins: 16 }
                spacing: 8
                Item {
                    width: parent.width; height: 28
                    Text { anchors.verticalCenter: parent.verticalCenter
                           text: Qt.locale().standaloneMonthName(bar.calMonth, Locale.LongFormat) + " " + bar.calYear
                           color: bar.theme.ink; font.pixelSize: 16; font.weight: 600 }
                    Row {
                        anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                        spacing: 4
                        Repeater {
                            model: [{ t: "‹", by: -1, name: "Previous month" }, { t: "Today", by: 0, name: "Today" }, { t: "›", by: 1, name: "Next month" }]
                            delegate: Rectangle {
                                required property var modelData
                                width: modelData.by === 0 ? 52 : 28; height: 28; radius: 8
                                color: navHover.hovered ? bar.theme.chip : "transparent"
                                Text { anchors.centerIn: parent; text: modelData.t; color: bar.theme.ink; font.pixelSize: modelData.by === 0 ? 12 : 18 }
                                HoverHandler { id: navHover; cursorShape: Qt.PointingHandCursor }
                                TapHandler { onTapped: { if (modelData.by === 0) { bar.calYear = clock.date.getFullYear(); bar.calMonth = clock.date.getMonth() } else bar.moveMonth(modelData.by) } }
                                Accessible.role: Accessible.Button
                                Accessible.name: modelData.name
                            }
                        }
                    }
                }
                Grid {
                    columns: 8
                    columnSpacing: 2; rowSpacing: 2
                    readonly property var names: [0, 1, 2, 3, 4, 5, 6].map(d => Qt.locale().dayName(d, Locale.NarrowFormat))
                    Text { width: 30; height: 22; text: "Wk"; color: bar.theme.inkSecondary; font.pixelSize: 10
                           horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter }
                    Repeater {
                        model: Cal.weekdayHeaders(parent.names, bar.firstDay)
                        delegate: Text {
                            required property var modelData
                            width: 34; height: 22; text: modelData; color: bar.theme.inkSecondary; font.pixelSize: 11
                            horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
                        }
                    }
                    Repeater {
                        // Each week: its number, then its seven days.
                        model: {
                            const days = Cal.monthGrid(bar.calYear, bar.calMonth, bar.firstDay), out = [];
                            for (let w = 0; w < 6; w++) {
                                out.push({ week: Cal.isoWeek(days[w * 7 + 3].year, days[w * 7 + 3].month, days[w * 7 + 3].day) });
                                for (let i = 0; i < 7; i++) out.push(days[w * 7 + i]);
                            }
                            return out;
                        }
                        delegate: Rectangle {
                            required property var modelData
                            readonly property bool isWeek: modelData.week !== undefined
                            readonly property bool today: !isWeek && Cal.isSameDay(modelData, clock.date)
                            width: isWeek ? 30 : 34; height: 32; radius: 16
                            color: today ? bar.theme.focus : "transparent"
                            Text {
                                anchors.centerIn: parent
                                text: parent.isWeek ? modelData.week : modelData.day
                                color: parent.today ? "#FFFFFF" : parent.isWeek ? bar.theme.inkSecondary
                                     : modelData.inMonth ? bar.theme.ink : bar.theme.inkSecondary
                                opacity: !parent.isWeek && !modelData.inMonth ? 0.5 : 1
                                font.pixelSize: parent.isWeek ? 10 : 13; font.weight: parent.today ? 600 : 400
                            }
                            Accessible.role: Accessible.StaticText
                            Accessible.name: parent.isWeek ? "Week " + modelData.week
                                : Qt.formatDate(new Date(modelData.year, modelData.month, modelData.day), "dddd d MMMM") + (today ? ", today" : "")
                        }
                    }
                }
            }
        }
    }
}
