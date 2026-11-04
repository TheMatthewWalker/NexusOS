import QtQuick

// A labelled text box for the installer: its problem (if any) shows under it
// once the person has typed something, so an empty form is not all red.
Column {
    id: field
    property var theme
    property string label
    property string placeholder: ""
    property string problem: ""
    property bool password: false
    property alias text: input.text
    signal accepted()
    function focusInput() { input.forceActiveFocus() }
    width: parent ? parent.width : 360
    spacing: 6
    Text { text: field.label; color: field.theme.inkSecondary; font.pixelSize: 13 }
    Rectangle {
        width: parent.width; height: 44; radius: 12
        color: field.theme.chip
        border.color: input.activeFocus ? field.theme.focus : field.problem && input.text ? field.theme.error : field.theme.glassBorder
        TextInput {
            id: input
            x: 14; width: parent.width - 28; anchors.verticalCenter: parent.verticalCenter
            color: field.theme.ink; font.pixelSize: 15; clip: true
            echoMode: field.password ? TextInput.Password : TextInput.Normal
            inputMethodHints: field.password ? Qt.ImhSensitiveData | Qt.ImhNoPredictiveText : Qt.ImhNone
            activeFocusOnTab: true
            onAccepted: field.accepted()
            Text { visible: !input.text; text: field.placeholder; color: field.theme.muted; font.pixelSize: 15 }
            Accessible.role: Accessible.EditableText
            Accessible.passwordEdit: field.password
            Accessible.name: field.label
        }
    }
    Text {
        visible: field.problem !== "" && input.text !== ""
        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: field.problem; color: field.theme.error; font.pixelSize: 12
    }
}
