import QtQuick

// A labelled text box for Settings; with password, what is typed is hidden
// and kept out of input-method predictions.
Column {
    id: field
    property var theme
    property string label
    property string placeholder: ""
    property bool password: false
    property alias text: input.text
    signal accepted()
    function clear() { input.text = "" }
    width: 320
    spacing: 4
    Text { text: field.label; color: field.theme.inkSecondary; font.pixelSize: 12 }
    Rectangle {
        width: parent.width; height: 36; radius: 10
        color: field.theme.chip
        border.color: input.activeFocus ? field.theme.focus : field.theme.glassBorder
        TextInput {
            id: input
            x: 12; width: parent.width - 24; anchors.verticalCenter: parent.verticalCenter
            color: field.theme.ink; font.pixelSize: 14; clip: true
            echoMode: field.password ? TextInput.Password : TextInput.Normal
            inputMethodHints: field.password ? Qt.ImhSensitiveData | Qt.ImhNoPredictiveText : Qt.ImhNone
            activeFocusOnTab: true
            onAccepted: field.accepted()
            Text { visible: !input.text; text: field.placeholder; color: field.theme.muted; font.pixelSize: 14 }
            Accessible.role: Accessible.EditableText
            Accessible.passwordEdit: field.password
            Accessible.name: field.label
        }
    }
}
