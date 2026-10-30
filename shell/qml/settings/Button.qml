import QtQuick

// A text button. primary fills it with the accent; danger outlines it in the
// error colour (for Shut down, Forget, Clear history).
Rectangle {
    id: btn
    property var theme
    property string label
    property bool primary: false
    property bool danger: false
    signal clicked()
    implicitWidth: text.implicitWidth + 28; implicitHeight: 34
    radius: 10
    opacity: enabled ? 1 : 0.5
    color: primary ? (hover.hovered ? Qt.lighter(theme.primary, 1.12) : theme.primary)
         : hover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.22) : "transparent"
    border.color: primary ? "transparent" : danger ? theme.error : activeFocus ? theme.focus : theme.glassBorder
    border.width: activeFocus ? 2 : 1
    activeFocusOnTab: true
    Keys.onReturnPressed: btn.clicked()
    Keys.onSpacePressed: btn.clicked()
    Text {
        id: text
        anchors.centerIn: parent
        text: btn.label
        color: btn.danger && !btn.primary ? btn.theme.error : btn.theme.ink
        font.pixelSize: 13; font.weight: 500
    }
    HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
    TapHandler { onTapped: btn.clicked() }
    Accessible.role: Accessible.Button
    Accessible.name: btn.label
    Accessible.onPressAction: btn.clicked()
}
