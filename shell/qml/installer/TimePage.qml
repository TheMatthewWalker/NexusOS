import QtQuick
import "../lib/installer.mjs" as I

// Time zone: the one the live system has (nexus-install's detection) is
// chosen; type to find another (timedatectl list-timezones).
Column {
    id: page
    property var app
    property var theme
    spacing: 12
    readonly property var shown: I.filterZones(page.app.zones, search.text)

    Text { text: "Time zone"; color: page.theme.ink; font.pixelSize: 24; font.weight: 600 }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        text: "Chosen: " + I.zoneLabel(page.app.timezone) + ". Type a city or region to change it."
    }
    Field { id: search; theme: page.theme; label: "Find a time zone"; placeholder: "London, New York, Tokyo…" }
    Rectangle {
        width: parent.width; height: 300; radius: 14
        color: page.theme.chip; border.color: page.theme.glassBorder
        clip: true
        ListView {
            id: list
            anchors.fill: parent; anchors.margins: 6
            model: page.shown
            boundsBehavior: Flickable.StopAtBounds
            delegate: Rectangle {
                required property var modelData
                width: list.width; height: 34; radius: 8
                color: page.app.timezone === modelData ? Qt.rgba(0.49, 0.42, 1.0, 0.22) : zoneHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.08) : "transparent"
                Text { x: 12; anchors.verticalCenter: parent.verticalCenter; text: I.zoneLabel(modelData)
                       color: page.theme.ink; font.pixelSize: 14 }
                HoverHandler { id: zoneHover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: page.app.timezone = modelData }
                Accessible.role: Accessible.RadioButton
                Accessible.checked: page.app.timezone === modelData
                Accessible.name: I.zoneLabel(modelData)
                Accessible.onPressAction: page.app.timezone = modelData
            }
        }
        Text {
            anchors.centerIn: parent; visible: page.shown.length === 0
            text: page.app.zones.length ? "No time zone matches." : "Loading time zones…"
            color: page.theme.inkSecondary; font.pixelSize: 13
        }
    }
}
