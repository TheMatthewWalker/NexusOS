import QtQuick
import "../lib/installer.mjs" as I

// Keyboard layout: the one guessed from the time zone (nexus-install's probe)
// is chosen; type to find another. Choosing one switches this session's
// keyboard at once, so the password typed on the next page is typed with it,
// as it will be when the computer starts and asks for it to unlock the disk.
Column {
    id: page
    property var app
    property var theme
    spacing: 12
    readonly property var layouts: page.app.probe ? (page.app.probe.keyboards || []) : []
    readonly property var shown: I.filterKeyboards(page.layouts, search.text)

    Text { text: "Keyboard"; color: page.theme.ink; font.pixelSize: 24; font.weight: 600 }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        text: "Chosen: " + I.keyboardName(page.app.keyboard, page.app.probe) + ". Your password is typed with this layout, "
              + "here and when the computer starts" + (page.app.encrypt ? " and asks for it to unlock the disk." : ".")
    }
    Field { id: search; theme: page.theme; label: "Find a layout"; placeholder: "English, German, French…" }
    Rectangle {
        width: parent.width; height: 240; radius: 14
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
                color: page.app.keyboard === modelData.id ? Qt.rgba(0.49, 0.42, 1.0, 0.22) : layoutHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.08) : "transparent"
                Text { x: 12; anchors.verticalCenter: parent.verticalCenter; text: modelData.name
                       color: page.theme.ink; font.pixelSize: 14 }
                HoverHandler { id: layoutHover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: page.app.setKeyboard(modelData.id) }
                Accessible.role: Accessible.RadioButton
                Accessible.checked: page.app.keyboard === modelData.id
                Accessible.name: modelData.name
                Accessible.onPressAction: page.app.setKeyboard(modelData.id)
            }
        }
        Text {
            anchors.centerIn: parent; visible: page.shown.length === 0
            text: page.layouts.length ? "No layout matches." : "Loading layouts…"
            color: page.theme.inkSecondary; font.pixelSize: 13
        }
    }
    Field { theme: page.theme; label: "Try it: type @ \" # £ \\ | here and check they come out as on your keys"; placeholder: "Type here" }
}
