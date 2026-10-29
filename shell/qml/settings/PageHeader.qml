import QtQuick

// A page's title and one sentence about it.
Column {
    id: header
    property var theme
    property string title
    property string summary: ""
    width: parent ? parent.width : 400
    spacing: 4
    bottomPadding: 8
    Text { text: header.title; color: header.theme.ink; font.pixelSize: 22; font.weight: 600 }
    Text {
        visible: header.summary !== ""
        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: header.summary; color: header.theme.inkSecondary; font.pixelSize: 13
    }
}
