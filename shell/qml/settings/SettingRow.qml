import QtQuick

// One setting: a label and a plain sentence on the left, its control on the
// right (the row's children). highlighted is set when search led here.
Rectangle {
    id: row
    property var theme
    property string label
    property string detail: ""
    property bool highlighted: false
    default property alias control: slot.data
    width: parent ? parent.width : 400
    implicitHeight: Math.max(56, texts.implicitHeight + 24)
    radius: 12
    color: highlighted ? Qt.rgba(0.49, 0.42, 1.0, 0.16) : row.theme.chip
    border.color: highlighted ? row.theme.focus : "transparent"
    Behavior on color { ColorAnimation { duration: 300 } }
    Column {
        id: texts
        x: 16; anchors.verticalCenter: parent.verticalCenter
        width: parent.width - slot.width - 48
        spacing: 3
        Text { width: parent.width; text: row.label; color: row.theme.ink; font.pixelSize: 14; font.weight: 500; elide: Text.ElideRight }
        Text {
            visible: row.detail !== ""
            width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
            text: row.detail; color: row.theme.inkSecondary; font.pixelSize: 12
        }
    }
    Item {
        id: slot
        anchors { right: parent.right; rightMargin: 16; verticalCenter: parent.verticalCenter }
        width: childrenRect.width; height: childrenRect.height
    }
}
