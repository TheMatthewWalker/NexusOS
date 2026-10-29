import QtQuick

// A segmented choice: options is a list of { value, label }.
Row {
    id: choice
    property var theme
    property var options: []
    property var current
    property string name: ""
    signal chose(var value)
    spacing: 4
    Repeater {
        model: choice.options
        delegate: Rectangle {
            required property var modelData
            readonly property bool on: modelData.value === choice.current
            implicitWidth: label.implicitWidth + 24; implicitHeight: 32
            radius: 9
            color: on ? choice.theme.primary : hover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.18) : "transparent"
            border.color: activeFocus ? choice.theme.focus : on ? "transparent" : choice.theme.glassBorder
            border.width: activeFocus ? 2 : 1
            activeFocusOnTab: true
            Keys.onSpacePressed: choice.chose(modelData.value)
            Keys.onReturnPressed: choice.chose(modelData.value)
            Text { id: label; anchors.centerIn: parent; text: modelData.label; color: choice.theme.ink; font.pixelSize: 13 }
            HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
            TapHandler { onTapped: choice.chose(modelData.value) }
            Accessible.role: Accessible.RadioButton
            Accessible.checked: on
            Accessible.name: choice.name + ": " + modelData.label
            Accessible.onPressAction: choice.chose(modelData.value)
        }
    }
}
