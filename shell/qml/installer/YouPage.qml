import QtQuick

// You: name, login name (suggested from the name), password twice (at least
// 10 characters: it also unlocks the disk and approves system changes), and
// the computer's name.
Column {
    id: page
    property var app
    property var theme
    spacing: 14
    function focusFirst() { fullName.focusInput() }

    Text { text: "Who will use this computer?"; color: page.theme.ink; font.pixelSize: 24; font.weight: 600 }
    Field {
        id: fullName
        theme: page.theme; label: "Your name"; placeholder: "Ada Lovelace"
        problem: page.app.problems.fullName
        onTextChanged: page.app.setFullName(text)
        onAccepted: login.focusInput()
    }
    Field {
        id: login
        theme: page.theme; label: "Login name"; placeholder: "ada"
        text: page.app.login
        problem: page.app.problems.login
        onTextChanged: if (text !== page.app.login) { page.app.login = text; page.app.loginEdited = true }
        onAccepted: password.focusInput()
    }
    Field {
        id: password
        theme: page.theme; label: "Password (at least 10 characters)"; password: true
        problem: page.app.password.length < 10 ? page.app.problems.password : ""
        onTextChanged: page.app.password = text
        onAccepted: again.focusInput()
    }
    Field {
        id: again
        theme: page.theme; label: "Password again"; password: true
        problem: page.app.password.length >= 10 ? page.app.problems.password : ""
        onTextChanged: page.app.again = text
        onAccepted: host.focusInput()
    }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 12
        text: "This password logs you in, approves system changes and, with encryption on, unlocks the disk each time the computer starts."
    }
    Field {
        id: host
        theme: page.theme; label: "Computer name"; placeholder: "nexus"
        text: page.app.host
        problem: page.app.problems.host
        onTextChanged: page.app.host = text
        onAccepted: page.app.next()
    }
}
