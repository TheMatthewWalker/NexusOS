// The NexusOS login screen: the same deep space, Nova core and glass as the
// desktop, instead of a text console. It runs in a small kiosk compositor
// (cage) started by greetd, talks to greetd through Quickshell, and quits once
// the desktop session is launched. nix/modules/desktop.nix falls back to the
// text greeter if this one cannot start, so nobody is ever locked out.
//   NEXUS_GREETER_USER, NEXUS_GREETER_NAME   the person to log in (nexusos.user)
//   NEXUS_GREETER_SESSION                    the command that starts the desktop
import QtQuick
import Quickshell
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

    function login(password) {
        if (busy || !user) return;
        busy = true; failed = false; message = "";
        pending = password;
        console.info("nexus-greeter: starting a session for " + user);
        Greetd.createSession(user);
    }
    Connections {
        target: Greetd
        function onAuthMessage(text, error, responseRequired, echoResponse) {
            console.info("nexus-greeter: greetd asks: " + text + (responseRequired ? " (needs an answer)" : ""));
            if (responseRequired) {
                // The first secret prompt is the password; anything else is shown and answered with it too.
                Greetd.respond(root.pending);
                root.pending = "";
            } else if (text) root.message = text;
        }
        function onAuthFailure(text) {
            console.info("nexus-greeter: login refused: " + text);
            root.busy = false; root.failed = true; root.pending = "";
            root.message = "That password did not work. Try again.";
            password.text = "";
            password.forceActiveFocus();
        }
        function onReadyToLaunch() {
            console.info("nexus-greeter: starting " + root.session.join(" "));
            Greetd.launch(root.session, [], true);
        }
        function onError(text) {
            console.warn("nexus-greeter: greetd error: " + text);
            root.busy = false; root.pending = "";
            root.message = "Could not log in: " + text;
        }
    }
    SystemClock { id: clock; precision: SystemClock.Minutes }

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
            Text { anchors.horizontalCenter: parent.horizontalCenter; text: Qt.formatDateTime(clock.date, "HH:mm")
                   color: root.theme.ink; font.pixelSize: 72; font.weight: 300 }
            Text { anchors.horizontalCenter: parent.horizontalCenter; text: Qt.formatDateTime(clock.date, "dddd d MMMM")
                   color: root.theme.inkSecondary; font.pixelSize: 16 }
        }

        Column {
            anchors.centerIn: parent
            anchors.verticalCenterOffset: 60
            spacing: 18
            width: 360

            NovaCore {
                anchors.horizontalCenter: parent.horizontalCenter
                width: 220; height: 160
                centre: Qt.point(110, 80)
                radius: 46
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
                visible: root.askUser
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
                visible: Quickshell.env("NEXUS_GREETER_USER") !== ""
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
