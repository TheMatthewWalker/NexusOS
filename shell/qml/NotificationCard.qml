import QtQuick
import Quickshell
import "lib/notifications.mjs" as N

// One notification: the app, its summary and text (plain text only), its
// actions as buttons, and a close button. Clicking the card runs its default action.
Rectangle {
    id: card
    property var theme
    property var notification
    signal activated()
    signal closed()
    readonly property var buttons: N.buttons(notification ? notification.actions : [])
    readonly property bool critical: !!notification && notification.urgency === 2
    implicitHeight: body.implicitHeight + 28
    radius: 16
    color: theme.glass
    border.color: critical ? theme.error : theme.glassBorder
    border.width: critical ? 2 : 1

    HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
    TapHandler { onTapped: card.activated() }

    Column {
        id: body
        x: 16; y: 14; width: parent.width - 52
        spacing: 4
        Row {
            spacing: 8
            Image {
                visible: !!card.notification && (card.notification.image !== "" || card.notification.appIcon !== "")
                width: 18; height: 18; sourceSize: Qt.size(36, 36)
                source: !card.notification ? "" : card.notification.image !== "" ? card.notification.image
                      : (N.iconSource(card.notification.appIcon) || Quickshell.iconPath(card.notification.appIcon, true))
            }
            Text { text: card.notification ? N.appName(card.notification.appName) : ""; color: card.theme.inkSecondary; font.pixelSize: 11 }
        }
        Text {
            width: parent.width; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
            textFormat: Text.PlainText
            text: card.notification ? card.notification.summary : ""
            color: card.theme.ink; font.pixelSize: 14; font.weight: 600
        }
        Text {
            visible: text !== ""
            width: parent.width; wrapMode: Text.Wrap; maximumLineCount: 4; elide: Text.ElideRight
            textFormat: Text.PlainText
            text: card.notification ? N.plain(card.notification.body) : ""
            color: card.theme.inkSecondary; font.pixelSize: 12; lineHeight: 1.2
        }
        Flow {
            visible: card.buttons.length > 0
            width: parent.width; spacing: 8; topPadding: 6
            Repeater {
                model: card.buttons
                delegate: Rectangle {
                    required property var modelData
                    height: 28; width: label.implicitWidth + 24; radius: 14
                    color: buttonHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.22) : card.theme.chip
                    border.color: card.theme.glassBorder
                    Text { id: label; anchors.centerIn: parent; text: modelData.text; color: card.theme.ink; font.pixelSize: 12
                           textFormat: Text.PlainText }
                    HoverHandler { id: buttonHover; cursorShape: Qt.PointingHandCursor }
                    TapHandler { onTapped: { modelData.invoke(); card.closed() } }
                    Accessible.role: Accessible.Button
                    Accessible.name: modelData.text
                }
            }
        }
    }
    Text {
        anchors { top: parent.top; right: parent.right; topMargin: 10; rightMargin: 14 }
        text: "×"; color: card.theme.inkSecondary; font.pixelSize: 18
        HoverHandler { cursorShape: Qt.PointingHandCursor }
        TapHandler { onTapped: card.closed() }
        Accessible.role: Accessible.Button
        Accessible.name: "Dismiss notification"
    }
    Accessible.role: Accessible.AlertMessage
    Accessible.name: (notification ? N.appName(notification.appName) + ": " + notification.summary + ". " + N.plain(notification.body) : "")
}
