import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/accounts.mjs" as A

// Settings, Accounts: your password (it also unlocks the disk at start-up,
// and changes there with it), and accounts for other people. The work is done
// as root by nexus-account (nix/modules/accounts.nix) through pkexec, which
// asks for your password (or an administrator's, to add or remove accounts).
// Passwords go to it on standard input, never on a command line.
// Cloud drives (OneDrive, Google Drive, Dropbox, Nextcloud as folders) are
// nexus-cloud's (nix/modules/online.nix), run as the person.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property string me: Quickshell.env("USER") || ""
    property var accounts: []
    property string message: ""
    property bool messageIsError: false
    property bool diskDiffers: false     // the disk kept a different password: ask for it
    property bool adding: false
    property string confirmRemove: ""
    property bool keepFiles: true

    function say(text, error) { message = text; messageIsError = !!error }
    function refresh() { lister.running = true }
    Component.onCompleted: { refresh(); refreshDrives() }

    Process {
        id: lister
        command: ["nexus-account"]
        stdout: StdioCollector { onStreamFinished: { const r = A.parse(text); if (r.accounts) page.accounts = r.accounts } }
    }
    // One root operation at a time: the command, then its request on standard input.
    Process {
        id: root
        property string request: ""
        property string operation: ""
        stdinEnabled: true
        stdout: StdioCollector { id: rootOut }
        onStarted: { write(request + "\n"); stdinEnabled = false }
        onExited: code => {
            const r = A.parse(rootOut.text);
            request = "";
            stdinEnabled = true;
            if (r.error || code !== 0) { page.say(r.error || A.refusal(code), true); return }
            page.say(r.message || "Done.", false);
            if (operation === "password") {
                page.diskDiffers = r.disk === "different";
                // The login keyring (saved passwords) is unlocked with the login
                // password: change its password too, as you (nix/modules/everyday.nix).
                keyring.input = current.text + "\n" + newPassword.text + "\n";
                keyring.running = true;
                current.clear(); newPassword.clear(); again.clear();
            } else if (operation === "disk") {
                page.diskDiffers = false; diskPassword.clear();
            } else {
                page.adding = false; page.confirmRemove = "";
                fullName.clear(); login.clear(); accountPassword.clear(); accountAgain.clear();
                page.refresh();
            }
        }
    }
    Process {
        id: keyring
        property string input: ""
        command: ["nexus-keyring-password"]
        stdinEnabled: true
        onStarted: { write(input); input = ""; stdinEnabled = false }
        onExited: stdinEnabled = true
    }
    function runAs(helper, operation, request) {
        if (root.running) return;
        root.operation = operation;
        root.request = JSON.stringify(request);
        root.command = [Quickshell.env("NEXUS_PKEXEC") || "/run/wrappers/bin/pkexec", "/run/current-system/sw/bin/" + helper, operation];
        root.running = true;
        say(A.working(operation), false);
    }

    PageHeader { theme: page.theme; title: "Accounts"; summary: "Your password, and accounts for other people on this computer." }

    Text {
        visible: page.message !== ""
        width: page.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.message; color: page.messageIsError ? page.theme.error : page.theme.inkSecondary; font.pixelSize: 13
    }

    SettingRow {
        theme: page.theme; label: "Your password"; highlighted: page.host.highlight === "password"
        detail: "It logs you in, approves system changes and unlocks the disk when the computer starts: all three change together."
    }
    Column {
        spacing: 8; leftPadding: 4
        TextField { id: current; theme: page.theme; label: "Current password"; password: true }
        TextField { id: newPassword; theme: page.theme; label: "New password (10 characters or more)"; password: true }
        TextField { id: again; theme: page.theme; label: "New password again"; password: true; onAccepted: changeButton.clicked() }
        Text {
            readonly property string problem: A.passwordProblem(newPassword.text, again.text)
            visible: problem !== "" && newPassword.text !== ""
            text: problem; color: page.theme.error; font.pixelSize: 12
        }
        Button {
            id: changeButton
            theme: page.theme; primary: true; label: "Change password"
            enabled: !root.running && current.text !== "" && A.passwordProblem(newPassword.text, again.text) === ""
            onClicked: page.runAs("nexus-account-password", "password", { current: current.text, new: newPassword.text })
        }
    }
    // The disk had a different password from the login one: bring it in step.
    Column {
        visible: page.diskDiffers
        spacing: 8; leftPadding: 4
        Text { width: page.width - 8; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
               text: "Enter the password the disk asks for at start-up, then your new password, to make them the same." }
        TextField { id: diskPassword; theme: page.theme; label: "The disk's current password"; password: true }
        TextField { id: diskNew; theme: page.theme; label: "Your new password"; password: true }
        Button { theme: page.theme; label: "Change the disk's password"
                 enabled: !root.running && diskPassword.text !== "" && diskNew.text.length >= A.MIN_PASSWORD
                 onClicked: page.runAs("nexus-account-password", "disk", { disk: diskPassword.text, new: diskNew.text }) }
    }

    SettingRow {
        theme: page.theme; label: "People on this computer"; highlighted: page.host.highlight === "accounts"
        detail: "Each person has their own files, settings and Nexus. Administrators can approve system changes."
        Button { theme: page.theme; visible: !page.adding; label: "Add an account"; onClicked: page.adding = true }
    }
    Repeater {
        model: page.accounts
        delegate: Rectangle {
            required property var modelData
            width: page.width; height: 52; radius: 10
            color: page.theme.chip
            Column {
                x: 16; anchors.verticalCenter: parent.verticalCenter; spacing: 2
                Text { text: A.title(modelData, page.me); color: page.theme.ink; font.pixelSize: 14; textFormat: Text.PlainText }
                Text { text: modelData.login + (modelData.admin ? " · administrator" : ""); color: page.theme.inkSecondary; font.pixelSize: 12 }
            }
            Row {
                anchors { right: parent.right; rightMargin: 12; verticalCenter: parent.verticalCenter }
                spacing: 8
                visible: A.removable(modelData, page.me)
                Button { theme: page.theme; visible: page.confirmRemove !== modelData.login; label: "Remove"
                         onClicked: { page.confirmRemove = modelData.login; page.keepFiles = true } }
                Button { theme: page.theme; visible: page.confirmRemove === modelData.login; label: page.keepFiles ? "Keep their files" : "Delete their files too"
                         onClicked: page.keepFiles = !page.keepFiles }
                Button { theme: page.theme; visible: page.confirmRemove === modelData.login; label: "Cancel"; onClicked: page.confirmRemove = "" }
                Button { theme: page.theme; visible: page.confirmRemove === modelData.login; primary: true; danger: true; label: "Remove " + modelData.login
                         onClicked: page.runAs("nexus-account-manage", "remove", { login: modelData.login, keepFiles: page.keepFiles }) }
            }
        }
    }
    Column {
        visible: page.adding
        spacing: 8; leftPadding: 4
        TextField { id: fullName; theme: page.theme; label: "Their name"
                    onTextChanged: if (!login.edited) login.text = A.suggestLogin(text) }
        TextField { id: login; theme: page.theme; label: "Login name"; property bool edited: false
                    onTextChanged: if (activeFocus) edited = true }
        TextField { id: accountPassword; theme: page.theme; label: "Their password (10 characters or more)"; password: true }
        TextField { id: accountAgain; theme: page.theme; label: "Their password again"; password: true }
        Row {
            spacing: 8
            Switch { id: admin; theme: page.theme; name: "Administrator"; checked: false; onToggled: checked = !checked }
            Text { anchors.verticalCenter: parent.verticalCenter; text: "Administrator (can approve system changes)"
                   color: page.theme.inkSecondary; font.pixelSize: 12 }
        }
        Text {
            readonly property string problem: A.loginProblem(login.text) || A.passwordProblem(accountPassword.text, accountAgain.text)
            visible: problem !== "" && (login.text !== "" || accountPassword.text !== "")
            text: problem; color: page.theme.error; font.pixelSize: 12
        }
        Row {
            spacing: 8
            Button { theme: page.theme; label: "Cancel"; onClicked: page.adding = false }
            Button { theme: page.theme; primary: true; label: "Add account"
                     enabled: !root.running && A.loginProblem(login.text) === "" && A.passwordProblem(accountPassword.text, accountAgain.text) === ""
                     onClicked: page.runAs("nexus-account-manage", "add", { login: login.text, fullName: fullName.text,
                                                                             password: accountPassword.text, admin: admin.checked }) }
        }
    }

    // ---- cloud drives ----
    readonly property string cloudCmd: Quickshell.env("NEXUS_CLOUD") || "nexus-cloud"
    property var drives: []
    property string cloudNote: ""
    property string cloudKind: ""
    readonly property var cloudKinds: [
        { kind: "onedrive", label: "OneDrive", name: "OneDrive" },
        { kind: "googledrive", label: "Google Drive", name: "GoogleDrive" },
        { kind: "dropbox", label: "Dropbox", name: "Dropbox" },
        { kind: "nextcloud", label: "Nextcloud", name: "Nextcloud" }
    ]
    function refreshDrives() { if (!cloudLister.running) cloudLister.running = true }
    Process { id: cloudLister; command: [page.cloudCmd, "list"]
              stdout: StdioCollector { onStreamFinished: { try { page.drives = JSON.parse(text) } catch (e) { page.drives = [] } } } }
    Process {
        id: cloudRunner
        property string input: ""
        stdinEnabled: true
        onStarted: { if (input !== "") write(input + "\n"); input = ""; stdinEnabled = false }
        onExited: stdinEnabled = true
        stdout: StdioCollector {
            onStreamFinished: {
                let r = {};
                try { r = JSON.parse(text) } catch (e) { r = { error: "nexus-cloud did not answer." } }
                page.cloudNote = r.error || r.message || "";
                if (!r.error) page.cloudKind = "";
                page.refreshDrives();
            }
        }
    }
    function addDrive(kind, name) {
        if (cloudRunner.running) return;
        cloudNote = "Sign in in the browser window that opens, and allow access. This page waits for it.";
        cloudRunner.command = [cloudCmd, "add", kind, name];
        cloudRunner.running = true;
    }
    function addNextcloud(name, url, user, appPassword) {
        if (cloudRunner.running) return;
        cloudNote = "Checking with Nextcloud…";
        cloudRunner.input = appPassword;
        cloudRunner.command = [cloudCmd, "add-nextcloud", name, url, user];
        cloudRunner.running = true;
    }
    function removeDrive(name) {
        if (cloudRunner.running) return;
        cloudRunner.command = [cloudCmd, "remove", name];
        cloudRunner.running = true;
    }

    SettingRow {
        theme: page.theme; label: "Cloud drives"; highlighted: page.host.highlight === "clouddrives"
        detail: "OneDrive, Google Drive, Dropbox or Nextcloud as a folder in your home folder and in Files, as on Windows. " +
                "Files open from a copy kept on this computer and changes upload by themselves."
    }
    Text { visible: page.cloudNote !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
           text: page.cloudNote; color: page.theme.inkSecondary; font.pixelSize: 12; leftPadding: 4 }
    Repeater {
        model: page.drives
        delegate: SettingRow {
            required property var modelData
            theme: page.theme; label: modelData.name + " · " + modelData.label
            detail: modelData.folder + (modelData.mounted ? "" : " (connecting…)")
            Row {
                spacing: 8
                Button { theme: page.theme; label: "Open"; onClicked: Quickshell.execDetached(["xdg-open", modelData.folder]) }
                Button { theme: page.theme; label: "Remove"; enabled: !cloudRunner.running; onClicked: page.removeDrive(modelData.name) }
            }
        }
    }
    Row {
        spacing: 8; leftPadding: 4
        Repeater {
            model: page.cloudKinds
            delegate: Button {
                required property var modelData
                theme: page.theme; label: "Add " + modelData.label; enabled: !cloudRunner.running
                onClicked: { page.cloudKind = modelData.kind; cloudName.text = A.freeDriveName(modelData.name, page.drives) }
            }
        }
    }
    Column {
        visible: page.cloudKind !== ""
        spacing: 8; leftPadding: 4
        TextField { id: cloudName; theme: page.theme; label: "Folder name (letters and digits)" }
        TextField { id: cloudUrl; visible: page.cloudKind === "nextcloud"; theme: page.theme; label: "Nextcloud address, e.g. https://cloud.example.com" }
        TextField { id: cloudUser; visible: page.cloudKind === "nextcloud"; theme: page.theme; label: "Nextcloud user name" }
        TextField { id: cloudPass; visible: page.cloudKind === "nextcloud"; theme: page.theme; password: true
                    label: "App password (Nextcloud: Settings, Security, Create new app password)" }
        Row {
            spacing: 8
            Button { theme: page.theme; label: "Cancel"; onClicked: page.cloudKind = "" }
            Button {
                theme: page.theme; primary: true; enabled: !cloudRunner.running && A.driveNameOk(cloudName.text)
                label: page.cloudKind === "nextcloud" ? "Connect" : "Sign in"
                onClicked: {
                    if (page.cloudKind === "nextcloud") { page.addNextcloud(cloudName.text, cloudUrl.text, cloudUser.text, cloudPass.text); cloudPass.clear() }
                    else page.addDrive(page.cloudKind, cloudName.text);
                }
            }
        }
    }
}
