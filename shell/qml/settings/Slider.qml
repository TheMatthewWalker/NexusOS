import QtQuick

// A 0..1 slider. moved(value) fires while dragging; the page applies it.
Item {
    id: slider
    property var theme
    property real value: 0
    property string name: ""
    property real step: 0.05
    signal moved(real value)
    implicitWidth: 220; implicitHeight: 26
    opacity: enabled ? 1 : 0.45
    activeFocusOnTab: true
    function set(v) { slider.moved(Math.max(0, Math.min(1, v))) }
    Keys.onLeftPressed: set(value - step)
    Keys.onRightPressed: set(value + step)
    Rectangle { anchors.verticalCenter: parent.verticalCenter; width: parent.width; height: 4; radius: 2; color: slider.theme.glassBorder }
    Rectangle { anchors.verticalCenter: parent.verticalCenter; width: parent.width * Math.min(1, slider.value); height: 4; radius: 2
                color: slider.theme.focus }
    Rectangle {
        x: parent.width * Math.min(1, slider.value) - width / 2; anchors.verticalCenter: parent.verticalCenter
        width: 16; height: 16; radius: 8; color: slider.theme.ink
        border.color: slider.activeFocus ? slider.theme.focus : "transparent"; border.width: 3
    }
    MouseArea {
        anchors.fill: parent
        cursorShape: Qt.PointingHandCursor
        onPressed: m => { slider.forceActiveFocus(); slider.set(m.x / width) }
        onPositionChanged: m => { if (pressed) slider.set(m.x / width) }
    }
    Accessible.role: Accessible.Slider
    Accessible.name: slider.name
}
