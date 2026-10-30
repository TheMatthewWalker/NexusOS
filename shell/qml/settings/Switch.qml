import QtQuick

// An on/off switch. It only reports clicks (toggled); the page decides what
// changes, so a switch never shows a state the system is not in.
Rectangle {
    id: sw
    property var theme
    property bool checked: false
    property string name: ""
    signal toggled()
    implicitWidth: 46; implicitHeight: 26
    radius: height / 2
    opacity: enabled ? 1 : 0.45
    color: checked ? sw.theme.primary : sw.theme.chip
    border.color: activeFocus ? sw.theme.focus : checked ? "transparent" : sw.theme.glassBorder
    border.width: activeFocus ? 2 : 1
    activeFocusOnTab: true
    Keys.onSpacePressed: sw.toggled()
    Keys.onReturnPressed: sw.toggled()
    Rectangle {
        width: 20; height: 20; radius: 10
        anchors.verticalCenter: parent.verticalCenter
        x: sw.checked ? parent.width - width - 3 : 3
        Behavior on x { NumberAnimation { duration: 140 } }
        color: sw.theme.ink
    }
    HoverHandler { cursorShape: Qt.PointingHandCursor }
    TapHandler { onTapped: sw.toggled() }
    Accessible.role: Accessible.CheckBox
    Accessible.checkable: true
    Accessible.checked: sw.checked
    Accessible.name: sw.name
    Accessible.onToggleAction: sw.toggled()
    Accessible.onPressAction: sw.toggled()
}
