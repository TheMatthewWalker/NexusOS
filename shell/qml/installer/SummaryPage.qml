import QtQuick
import "../lib/installer.mjs" as I

// Summary: everything that will happen, with the hardware scan's lines
// (Processor, Graphics, Local AI) and the encryption choice. Nothing has
// changed on the computer yet.
Column {
    id: page
    property var app
    property var theme
    spacing: 10

    Text { text: "Ready to install"; color: page.theme.ink; font.pixelSize: 24; font.weight: 600 }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        text: "Nothing has changed yet. Check these, then press " + (page.app.partition ? "Replace and install." : page.app.alongside ? "Install." : "Erase and install.")
    }
    Repeater {
        model: I.summaryRows(page.app.summaryState(), page.app.probe)
        delegate: Row {
            required property var modelData
            width: page.width; spacing: 16
            Text { width: 110; text: modelData.label; color: page.theme.inkSecondary; font.pixelSize: 13 }
            Text { width: parent.width - 126; text: modelData.value; wrapMode: Text.Wrap; textFormat: Text.PlainText
                   color: modelData.danger ? page.theme.error : page.theme.ink; font.pixelSize: 13 }
        }
    }
    Repeater {
        model: page.app.partition ? [] : page.app.alongside ? (page.app.choices.alongside.warnings || []) : []
        delegate: Text { required property var modelData; width: page.width; wrapMode: Text.Wrap
                         text: modelData; color: "#FFD166"; font.pixelSize: 12 }
    }
    Item { width: 1; height: 6 }
    Card {
        theme: page.theme
        check: true
        title: "Encrypt the disk"
        detail: "Recommended. A lost or stolen computer shows nothing without your password; you type it once when the computer starts."
        selected: page.app.encrypt
        onChosen: page.app.encrypt = !page.app.encrypt
    }
}
