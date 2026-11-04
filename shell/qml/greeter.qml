// The NexusOS login screen: the same deep space, Nova core and glass as the
// desktop, instead of a text console. It runs in a small kiosk compositor
// (cage) started by greetd, talks to greetd through Quickshell, and quits once
// the desktop session is launched. nix/modules/desktop.nix falls back to the
// text greeter if this one cannot start or stops answering, so nobody is ever
// locked out.
//   NEXUS_GREETER_USER, NEXUS_GREETER_NAME   the person to log in (nexusos.user)
//   NEXUS_GREETER_SESSION                    the command that starts the desktop
//   NEXUS_GREETER_HEARTBEAT                  a file touched every 5 s for the watchdog
import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Services.Greetd

ShellRoot {
    id: root
    readonly property var theme: ({
        ink: "#E3EAFF", inkSecondary: "#9AA9C9", glass: Qt.rgba(0.04, 0.07, 0.15, 0.80),
        glassBorder: "#2A3A66", focus: "#86A8FF", primary: "#7C6BFF", error: "#FF8FA3"
    })
    property string user: Quickshell.env("NEXUS_GREETER_USER") || ""
    readonly property string fullName: user === Quickshell.env("NEXUS_GREETER_USER") ? (Quickshell.env("NEXUS_GREETER_NAME") || "") : ""
    readonly property var session: (Quickshell.env("NEXUS_GREETER_SESSION") || "Hyprland").split(" ")
    property bool askUser: user === ""
    property string message: ""
    property bool failed: false
    property bool busy: false
    property string pending: ""          // the password, held only until greetd asks for it
    property int answered: 0             // secret questions answered in this conversation

    // An expired password. greetd (patched in nix/modules/desktop.nix) then
    // runs PAM's password change, which asks for the current password, the
    // new one and the new one again; the greeter answers those from the form
    // below and the login carries on. The answers are held only until the
    // login is done or refused, and are never logged.
    property bool expired: false
    property bool changing: false        // the form was sent; its answers are held
    property string heldCurrent: ""
    property string heldNew: ""
    property string heldAgain: ""
    property string parkedPrompt: ""     // a PAM question that waits for the form
    property string pamNote: ""          // what PAM said about the new password, if anything
    readonly property int minLength: 10  // as the installer asks: the password also approves system changes
    // pam_unix's words ("You are required to change your password immediately
    // (password expired)", "... (administrator enforced)") and the PAM code greetd passes on.
    readonly property var expiryPattern: /change your password|password (has )?expired|NEW_AUTHTOK_REQD|token is no longer valid/i
    function kindOf(prompt) {
        if (/retype|again|repeat|confirm|re-enter/i.test(prompt)) return "again";
        if (/new/i.test(prompt)) return "new";
        return "current";                 // "Password:", "Current password:", "(current) UNIX password:"
    }
    function answerFor(prompt) {
        const kind = kindOf(prompt);
        console.info("nexus-greeter: answering the " + kind + " password question");
        return kind === "again" ? heldAgain : kind === "new" ? heldNew : heldCurrent;
    }
    function clearHeld() { heldCurrent = ""; heldNew = ""; heldAgain = ""; changing = false }
    function resetChange() {
        clearHeld(); expired = false; parkedPrompt = ""; pamNote = "";
        currentField.text = ""; newField.text = ""; againField.text = "";
    }

    function login(password) {
        if (busy || !user) return;
        busy = true; failed = false; message = ""; answered = 0;
        pending = password;
        console.info("nexus-greeter: starting a session for " + user);
        Greetd.createSession(user);
    }
    // The form: current, new, new again. Checked here first so a slip does
    // not cost a round with PAM; PAM still has the last word.
    function changePassword() {
        if (busy) return;
        const problem = !currentField.text ? "Type your current password."
            : newField.text.length < minLength ? "Use at least " + minLength + " characters for the new password."
            : newField.text !== againField.text ? "The two new passwords are not the same."
            : newField.text === currentField.text ? "Choose a new password that is different from the old one." : "";
        if (problem) {
            message = problem; failed = true;
            if (!currentField.text) currentField.forceActiveFocus();
            else if (newField.text.length < minLength || newField.text === currentField.text) {
                newField.text = ""; againField.text = ""; newField.forceActiveFocus();
            }
            else { againField.text = ""; againField.forceActiveFocus() }
            return;
        }
        heldCurrent = currentField.text; heldNew = newField.text; heldAgain = againField.text;
        changing = true; failed = false; pamNote = ""; message = ""; busy = true;
        if (parkedPrompt) {
            // PAM is already waiting for an answer.
            const prompt = parkedPrompt; parkedPrompt = "";
            answered++;
            Greetd.respond(answerFor(prompt));
        } else {
            // Start again: log in with the current password, then answer the change.
            answered = 0;
            console.info("nexus-greeter: starting a session for " + user + " to change the password");
            Greetd.createSession(user);
        }
    }
    function cancelChange() {
        console.info("nexus-greeter: password change cancelled");
        if (parkedPrompt || busy) Greetd.cancelSession();
        resetChange(); busy = false; failed = false; message = ""; password.text = "";
        password.forceActiveFocus();
    }
    // The form appears with the password already typed as the current one.
    function showChange() {
        expired = true; busy = false; failed = false; message = "";
        if (!currentField.text) currentField.text = password.text;
        password.text = "";
        (currentField.text ? newField : currentField).forceActiveFocus();
    }

    Connections {
        target: Greetd
        function onAuthMessage(text, error, responseRequired, echoResponse) {
            console.info("nexus-greeter: greetd asks: " + text + (responseRequired ? " (needs an answer)" : ""));
            if (root.expiryPattern.test(text) && !root.expired) {
                console.info("nexus-greeter: the password has expired");
                root.expired = true;
            }
            if (!responseRequired) {
                if (!text || root.expiryPattern.test(text)) return;
                if (root.expired) root.pamNote = text;   // e.g. "BAD PASSWORD: ...", "Password unchanged"
                else root.message = text;
                return;
            }
            if (root.changing) { root.answered++; Greetd.respond(root.answerFor(text)); return }
            if (root.answered === 0) {
                // The first secret question is the password.
                root.answered++;
                Greetd.respond(root.pending);
                root.pending = "";
                return;
            }
            if (root.expired) {
                // PAM asks for the change before the form is filled in: it waits for it.
                root.parkedPrompt = text;
                root.showChange();
                return;
            }
            // Anything else is answered with the password, as before.
            root.answered++;
            Greetd.respond(root.pending);
        }
        function onAuthFailure(text) {
            console.info("nexus-greeter: login refused: " + text);
            const wasChanging = root.changing;
            root.busy = false; root.failed = true; root.pending = ""; root.parkedPrompt = "";
            root.clearHeld();
            if (/ACCT_EXPIRED/.test(text)) {
                root.resetChange();
                root.message = "This account has expired. Ask whoever looks after this computer to renew it.";
                password.forceActiveFocus();
            } else if (wasChanging && /pam_authenticate|AUTH_ERR/.test(text)) {
                root.message = "Your current password did not work. Try again.";
                currentField.text = ""; currentField.forceActiveFocus();
            } else if (wasChanging && /NEW_AUTHTOK_REQD/.test(text)) {
                // greetd did not offer the change (a greetd without the patch): say how to do it instead.
                root.message = "This login screen could not change it. Press Ctrl+Alt+F2, log in there and choose a new password, then come back with Ctrl+Alt+F1.";
            } else if (wasChanging) {
                root.message = "Your password was not changed" + (root.pamNote ? ": " + root.pamNote : ".") + " Try a different one.";
                newField.text = ""; againField.text = ""; newField.forceActiveFocus();
            } else if (root.expired || root.expiryPattern.test(text)) {
                root.showChange();
            } else {
                root.message = "That password did not work. Try again.";
                password.text = "";
                password.forceActiveFocus();
            }
        }
        function onReadyToLaunch() {
            if (root.changing) console.info("nexus-greeter: password changed");
            root.resetChange();
            console.info("nexus-greeter: starting " + root.session.join(" "));
            Greetd.launch(root.session, [], true);
        }
        // greetd starts the desktop once the login screen has exited.
        function onLaunched() { console.info("nexus-greeter: launched; exiting"); quitSoon.start() }
        function onError(text) {
            console.warn("nexus-greeter: greetd error: " + text);
            // After a refused password the connection to greetd is reset;
            // the message already shown is the one that matters.
            if (root.failed || root.expired) { root.busy = false; return }
            root.busy = false; root.pending = "";
            root.message = "Could not log in: " + text;
        }
    }
    // A heartbeat for the watchdog in nix/modules/desktop.nix: if the login
    // screen stops answering, greetd's text login takes over.
    readonly property string heartbeatPath: Quickshell.env("NEXUS_GREETER_HEARTBEAT") || ""
    FileView { id: heartbeat; path: root.heartbeatPath; printErrors: false }
    Timer {
        interval: 5000; repeat: true; triggeredOnStart: true; running: root.heartbeatPath !== ""
        onTriggered: heartbeat.setText(String(Date.now()))
    }
    SystemClock { id: clock; precision: SystemClock.Minutes }
    Timer { id: quitSoon; interval: 500; onTriggered: Qt.quit() }

    // A password box: glass, round, the text hidden.
    component Secret: FocusScope {
        id: secret
        property alias text: input.text
        property string placeholder
        property bool bad: false
        signal accepted()
        width: parent ? parent.width : 360; height: 52
        Rectangle {
            anchors.fill: parent; radius: 26; color: root.theme.glass
            border.color: secret.bad ? root.theme.error : input.activeFocus ? root.theme.focus : root.theme.glassBorder
        }
        TextInput {
            id: input
            x: 24; width: parent.width - 48; anchors.verticalCenter: parent.verticalCenter
            focus: true
            echoMode: TextInput.Password
            color: root.theme.ink; font.pixelSize: 15; clip: true
            enabled: !root.busy
            inputMethodHints: Qt.ImhSensitiveData | Qt.ImhNoPredictiveText
            Text { visible: !input.text; text: secret.placeholder; color: root.theme.inkSecondary; font.pixelSize: 15 }
            onAccepted: secret.accepted()
            Accessible.role: Accessible.EditableText
            Accessible.passwordEdit: true
            Accessible.name: secret.placeholder
        }
    }

    FloatingWindow {
        id: win
        visible: true
        color: "#04050C"
        title: "NexusOS"

        Rectangle {
            anchors.fill: parent
            gradient: Gradient {
                GradientStop { position: 0; color: "#0B0F24" }
                GradientStop { position: 0.55; color: "#05071A" }
                GradientStop { position: 1; color: "#020309" }
            }
        }
        Canvas {
            anchors.fill: parent
            onWidthChanged: requestPaint()
            onHeightChanged: requestPaint()
            onPaint: {
                const ctx = getContext("2d");
                ctx.reset();
                let seed = 5;
                const rnd = () => { seed = (seed * 1664525 + 1013904223) % 4294967296; return seed / 4294967296 };
                for (let i = 0; i < 140; i++) {
                    ctx.fillStyle = "rgba(201,212,255," + (0.2 + rnd() * 0.6).toFixed(2) + ")";
                    const s = rnd() < 0.85 ? 1 : 2;
                    ctx.fillRect(Math.round(rnd() * width), Math.round(rnd() * height), s, s);
                }
            }
        }
        Nebula { width: 900; height: 760; anchors.centerIn: parent; anchors.verticalCenterOffset: 40; seed: 11; depth: 0.7 }

        Column {
            anchors.horizontalCenter: parent.horizontalCenter
            y: Math.max(40, parent.height * 0.12)
            spacing: 6
            // The change form is taller: the clock steps aside on a small screen.
            visible: !root.expired || win.height > 820
            Text { anchors.horizontalCenter: parent.horizontalCenter; text: Qt.formatDateTime(clock.date, "HH:mm")
                   color: root.theme.ink; font.pixelSize: 72; font.weight: 300 }
            Text { anchors.horizontalCenter: parent.horizontalCenter; text: Qt.formatDateTime(clock.date, "dddd d MMMM")
                   color: root.theme.inkSecondary; font.pixelSize: 16 }
        }

        Column {
            anchors.centerIn: parent
            anchors.verticalCenterOffset: root.expired ? 20 : 60
            spacing: 18
            width: 360

            NovaCore {
                anchors.horizontalCenter: parent.horizontalCenter
                width: 220; height: root.expired ? 120 : 160
                centre: Qt.point(110, height / 2)
                radius: root.expired ? 36 : 46
                steps: 24
                idleFps: 30
                mode: root.busy ? "thinking" : "idle"
            }
            Text {
                anchors.horizontalCenter: parent.horizontalCenter
                visible: !root.askUser
                text: root.fullName || root.user
                color: root.theme.ink; font.pixelSize: 22; font.weight: 550
            }
            // Someone else: type a user name first.
            Rectangle {
                visible: root.askUser && !root.expired
                width: parent.width; height: 52; radius: 26
                color: root.theme.glass; border.color: userField.activeFocus ? root.theme.focus : root.theme.glassBorder
                TextInput {
                    id: userField
                    x: 24; width: parent.width - 48; anchors.verticalCenter: parent.verticalCenter
                    color: root.theme.ink; font.pixelSize: 15; clip: true
                    focus: root.askUser
                    Text { visible: !userField.text; text: "User name"; color: root.theme.inkSecondary; font.pixelSize: 15 }
                    onAccepted: if (text.trim()) { root.user = text.trim(); password.forceActiveFocus() }
                    Keys.onTabPressed: { root.user = text.trim(); password.forceActiveFocus() }
                    Accessible.role: Accessible.EditableText
                    Accessible.name: "User name"
                }
            }
            Rectangle {
                visible: !root.expired
                width: parent.width; height: 52; radius: 26
                color: root.theme.glass
                border.color: root.failed ? root.theme.error : password.activeFocus ? root.theme.focus : root.theme.glassBorder
                TextInput {
                    id: password
                    x: 24; width: parent.width - 80; anchors.verticalCenter: parent.verticalCenter
                    echoMode: TextInput.Password
                    color: root.theme.ink; font.pixelSize: 15; clip: true
                    focus: !root.askUser
                    enabled: !root.busy
                    inputMethodHints: Qt.ImhSensitiveData | Qt.ImhNoPredictiveText
                    Text { visible: !password.text; text: root.busy ? "Logging in…" : "Password"
                           color: root.theme.inkSecondary; font.pixelSize: 15 }
                    onAccepted: { if (root.askUser) root.user = userField.text.trim(); root.login(text) }
                    onActiveFocusChanged: console.info("nexus-greeter: password box " + (activeFocus ? "has" : "lost") + " the keyboard")
                    Accessible.role: Accessible.EditableText
                    Accessible.passwordEdit: true
                    Accessible.name: "Password"
                }
                Rectangle {
                    width: 40; height: 40; radius: 20
                    anchors { right: parent.right; rightMargin: 6; verticalCenter: parent.verticalCenter }
                    color: root.busy ? root.theme.glassBorder : root.theme.primary
                    Text { anchors.centerIn: parent; text: "→"; color: "#FFFFFF"; font.pixelSize: 18 }
                    TapHandler { enabled: !root.busy; onTapped: { if (root.askUser) root.user = userField.text.trim(); root.login(password.text) } }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Log in"
                }
            }

            // The password has expired: current, new, new again, then the login carries on.
            Column {
                visible: root.expired
                width: parent.width; spacing: 12
                Text {
                    width: parent.width; horizontalAlignment: Text.AlignHCenter
                    text: "Your password has expired"; color: root.theme.ink; font.pixelSize: 18; font.weight: 600
                    Accessible.role: Accessible.Heading
                    Accessible.name: text
                }
                Text {
                    width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
                    text: "Choose a new one to carry on. Use at least " + root.minLength + " characters: it also approves system changes."
                    color: root.theme.inkSecondary; font.pixelSize: 13
                }
                Secret {
                    id: currentField; placeholder: "Current password"
                    bad: root.failed && root.message.indexOf("current") >= 0
                    onAccepted: newField.forceActiveFocus()
                    KeyNavigation.tab: newField
                }
                Secret {
                    id: newField; placeholder: "New password"
                    bad: root.failed && root.message.indexOf("current") < 0
                    onAccepted: againField.forceActiveFocus()
                    KeyNavigation.tab: againField
                }
                Secret {
                    id: againField; placeholder: "New password again"
                    bad: root.failed && root.message.indexOf("same") >= 0
                    onAccepted: root.changePassword()
                    KeyNavigation.tab: currentField
                }
                Row {
                    anchors.horizontalCenter: parent.horizontalCenter
                    spacing: 10
                    Rectangle {
                        width: changeLabel.implicitWidth + 36; height: 40; radius: 20
                        color: root.busy ? root.theme.glassBorder : root.theme.primary
                        Text { id: changeLabel; anchors.centerIn: parent; color: "#FFFFFF"; font.pixelSize: 14; font.weight: 550
                               text: root.busy ? "Changing…" : "Change and log in" }
                        TapHandler { enabled: !root.busy; onTapped: root.changePassword() }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Change password and log in"
                    }
                    Rectangle {
                        width: cancelLabel.implicitWidth + 32; height: 40; radius: 20
                        color: root.theme.glass; border.color: root.theme.glassBorder
                        Text { id: cancelLabel; anchors.centerIn: parent; text: "Cancel"; color: root.theme.ink; font.pixelSize: 14 }
                        TapHandler { onTapped: root.cancelChange() }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Cancel"
                    }
                }
            }
            Text {
                anchors.horizontalCenter: parent.horizontalCenter
                width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
                visible: root.message !== ""
                text: root.message; textFormat: Text.PlainText
                color: root.failed ? root.theme.error : root.theme.inkSecondary; font.pixelSize: 13
            }
            Text {
                anchors.horizontalCenter: parent.horizontalCenter
                text: root.askUser ? "Back" : "Someone else?"
                color: root.theme.focus; font.pixelSize: 13
                visible: Quickshell.env("NEXUS_GREETER_USER") !== "" && !root.expired
                TapHandler {
                    onTapped: {
                        root.askUser = !root.askUser;
                        root.user = root.askUser ? "" : Quickshell.env("NEXUS_GREETER_USER");
                        root.message = ""; root.failed = false;
                        (root.askUser ? userField : password).forceActiveFocus();
                    }
                }
            }
        }

        // Restart and shut down, bottom right.
        Row {
            anchors { right: parent.right; bottom: parent.bottom; margins: 28 }
            spacing: 10
            Repeater {
                model: [{ label: "Restart", command: "reboot" }, { label: "Shut down", command: "poweroff" }]
                delegate: Rectangle {
                    required property var modelData
                    width: label.implicitWidth + 28; height: 36; radius: 12
                    color: hover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.22) : root.theme.glass
                    border.color: root.theme.glassBorder
                    Text { id: label; anchors.centerIn: parent; text: modelData.label; color: root.theme.ink; font.pixelSize: 13 }
                    HoverHandler { id: hover }
                    TapHandler { onTapped: Quickshell.execDetached(["systemctl", modelData.command]) }
                    Accessible.role: Accessible.Button
                    Accessible.name: modelData.label
                }
            }
        }
        Text {
            anchors { left: parent.left; bottom: parent.bottom; margins: 28 }
            text: "NexusOS"; color: root.theme.inkSecondary; font.pixelSize: 13; font.weight: 600; font.letterSpacing: 2
        }
    }
}
