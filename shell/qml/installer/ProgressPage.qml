import QtQuick

// Install: nexus-install's steps as they happen (--progress-json), with the
// line it is working on. A failure shows nexus-install's own message, the
// log's last lines and where the full log is.
Column {
    id: page
    property var app
    property var theme
    readonly property var p: page.app.progress
    spacing: 14

    Text {
        text: page.p.phase === "failed" ? "The installation stopped" : page.p.phase === "done" ? "NexusOS is installed" : "Installing NexusOS"
        color: page.p.phase === "failed" ? page.theme.error : page.theme.ink; font.pixelSize: 24; font.weight: 600
    }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        visible: page.p.phase !== "failed"
        text: (page.p.n > 0 ? "Step " + page.p.n + " of " + page.p.of + ": " + page.p.title : "Starting…")
    }
    Rectangle {
        visible: page.p.phase !== "failed"
        width: parent.width; height: 10; radius: 5; color: page.theme.chip
        Rectangle {
            width: Math.max(10, parent.width * page.p.fraction); height: parent.height; radius: 5
            color: page.theme.primary
            Behavior on width { NumberAnimation { duration: 400 } }
        }
        Accessible.role: Accessible.ProgressBar
        Accessible.name: "Installation progress"
    }
    Text {
        visible: page.p.phase === "running" && page.p.detail !== ""
        width: parent.width; elide: Text.ElideMiddle; textFormat: Text.PlainText
        text: page.p.detail; color: page.theme.muted; font.pixelSize: 12; font.family: "monospace"
    }
    Text {
        visible: page.p.phase === "running"
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 12
        text: "This takes 5 to 30 minutes. Closing this window does not stop it; Install NexusOS opens it again."
    }

    // A failure: the real error, then the log.
    Text {
        visible: page.p.phase === "failed"
        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.p.error; color: page.theme.ink; font.pixelSize: 14
    }
    Rectangle {
        visible: page.p.phase === "failed" && page.p.tail.length > 0
        width: parent.width; height: Math.min(220, tailText.implicitHeight + 20); radius: 12
        color: page.theme.chip; border.color: page.theme.glassBorder; clip: true
        Flickable {
            anchors.fill: parent; anchors.margins: 10
            contentHeight: tailText.implicitHeight
            contentY: Math.max(0, contentHeight - height)
            Text {
                id: tailText
                width: parent.width; wrapMode: Text.WrapAnywhere; textFormat: Text.PlainText
                text: page.p.tail.join("\n"); color: page.theme.inkSecondary; font.pixelSize: 11; font.family: "monospace"
            }
        }
    }
    Text {
        visible: page.p.phase === "failed"
        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: "The full log is in " + (page.p.log || "/tmp/nexus-install.log") +
              ". You can go back and try again" + (page.app.alongside ? ": the partition it added has been removed, and the rest of the disk is as it was." : ".")
        color: page.theme.inkSecondary; font.pixelSize: 12
    }
}
