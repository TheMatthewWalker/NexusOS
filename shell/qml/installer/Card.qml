import QtQuick

// A choice in the installer (a disk, Erase or Alongside, encryption): a title,
// a sentence, and a ring when chosen. danger marks the choice that erases.
Rectangle {
    id: card
    property var theme
    property string title
    property string detail: ""
    property string note: ""             // a warning under the detail
    property bool selected: false
    property bool danger: false
    property bool check: false           // a tick box instead of a ring
    signal chosen()
    width: parent ? parent.width : 400
    implicitHeight: texts.implicitHeight + 28
    radius: 14
    opacity: enabled ? 1 : 0.5
    color: selected ? Qt.rgba(0.49, 0.42, 1.0, 0.16) : hover.hovered && enabled ? Qt.rgba(0.49, 0.42, 1.0, 0.08) : card.theme.chip
    border.color: activeFocus ? card.theme.focus : selected ? (danger ? card.theme.error : card.theme.primary) : card.theme.glassBorder
    border.width: selected || activeFocus ? 2 : 1
    activeFocusOnTab: enabled
    Keys.onReturnPressed: card.chosen()
    Keys.onSpacePressed: card.chosen()
    Rectangle {
        id: mark
        x: 16; y: 16; width: 18; height: 18; radius: card.check ? 5 : 9
        color: card.selected ? (card.danger ? card.theme.error : card.theme.primary) : "transparent"
        border.color: card.selected ? "transparent" : card.theme.inkSecondary
        Text { anchors.centerIn: parent; visible: card.selected && card.check; text: "✓"; color: "#FFFFFF"; font.pixelSize: 12 }
    }
    Column {
        id: texts
        x: 46; y: 14; width: parent.width - 62
        spacing: 4
        Text { width: parent.width; text: card.title; color: card.danger && card.selected ? card.theme.error : card.theme.ink
               font.pixelSize: 15; font.weight: 550; wrapMode: Text.Wrap; textFormat: Text.PlainText }
        Text { visible: card.detail !== ""; width: parent.width; text: card.detail; color: card.theme.inkSecondary
               font.pixelSize: 12; wrapMode: Text.Wrap; textFormat: Text.PlainText; lineHeight: 1.2 }
        Text { visible: card.note !== ""; width: parent.width; text: card.note; color: "#FFD166"
               font.pixelSize: 12; wrapMode: Text.Wrap; textFormat: Text.PlainText; lineHeight: 1.2 }
    }
    HoverHandler { id: hover; enabled: card.enabled; cursorShape: Qt.PointingHandCursor }
    TapHandler { enabled: card.enabled; onTapped: card.chosen() }
    Accessible.role: card.check ? Accessible.CheckBox : Accessible.RadioButton
    Accessible.checkable: true
    Accessible.checked: card.selected
    Accessible.name: card.title
    Accessible.description: card.detail
    Accessible.onPressAction: card.chosen()
}
