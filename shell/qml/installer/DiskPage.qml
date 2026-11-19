import QtQuick
import "../lib/installer.mjs" as I

// Disk: the computer's disks (from nexus-install --probe-json), then what to
// do with the chosen one: install alongside what is there, in its free space;
// replace one of its partitions; or erase it. Each is offered only when
// nexus-install's plan says it is safe; otherwise its reason is shown (often:
// make room in Windows first). Everything is chosen here: no terminal.
Column {
    id: page
    property var app
    property var theme
    spacing: 12

    Text { text: "Where should NexusOS go?"; color: page.theme.ink; font.pixelSize: 24; font.weight: 600 }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        visible: page.app.probing || page.app.probeError !== ""
        text: page.app.probing ? "Looking at this computer's disks…" : page.app.probeError
    }
    Text {
        width: parent.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        visible: !!page.app.probe && page.app.probe.disks.length === 0
        text: "No disks were found. Is the disk switched on in the firmware settings (some laptops need RAID/RST set to AHCI)?"
    }
    Repeater {
        model: page.app.probe ? page.app.probe.disks : []
        delegate: Card {
            required property var modelData
            theme: page.theme
            title: I.diskTitle(modelData)
            detail: I.diskDetail(modelData)
            enabled: !modelData.inUse
            selected: page.app.disk === modelData.path
            onChosen: page.app.chooseDisk(modelData.path)
        }
    }

    Item { width: 1; height: 8; visible: page.app.disk !== "" }
    Text { visible: page.app.disk !== ""; text: "What to do with it"; color: page.theme.ink; font.pixelSize: 16; font.weight: 600 }
    Card {
        visible: page.app.disk !== ""
        theme: page.theme
        title: "Install alongside " + (page.app.choices.alongside.systems || "what is there")
        detail: page.app.choices.alongside.possible
            ? page.app.choices.alongside.reason
            : (page.app.choices.alongside.reason || "This disk has nothing to install beside.")
        note: (page.app.choices.alongside.warnings || []).join("\n")
        enabled: page.app.choices.alongside.possible
        selected: page.app.alongside && page.app.partition === ""
        onChosen: { page.app.alongside = true; page.app.partition = "" }
    }
    Card {
        visible: page.app.disk !== ""
        theme: page.theme
        danger: true
        title: "Replace a partition"
        detail: page.app.choices.replace.possible
            ? "Choose one partition below: only it is deleted, and NexusOS goes in its place. The rest of the disk is not changed."
            : (page.app.choices.replace.reason || "No partition on this disk can be replaced.")
        enabled: page.app.choices.replace.possible
        selected: page.app.partition !== ""
        onChosen: {
            const first = page.app.choices.replace.partitions.find(p => p.possible);
            if (first) { page.app.alongside = false; page.app.partition = first.path }
        }
    }
    Repeater {
        model: page.app.disk !== "" && page.app.choices.replace.possible ? page.app.choices.replace.partitions : []
        delegate: Card {
            required property var modelData
            x: 28; width: page.width - 28
            theme: page.theme
            danger: true
            title: modelData.path + ": " + I.partitionTitle(modelData)
            detail: modelData.possible ? (modelData.warning || "") : modelData.reason
            enabled: modelData.possible
            selected: page.app.partition === modelData.path
            onChosen: { page.app.alongside = false; page.app.partition = modelData.path }
        }
    }
    Card {
        visible: page.app.disk !== ""
        theme: page.theme
        danger: true
        title: "Erase this disk"
        detail: page.app.choices.erase.possible
            ? "Everything on " + (page.app.diskInfo ? I.diskTitle(page.app.diskInfo) : "it") + " is deleted, and NexusOS uses all of it."
            : page.app.choices.erase.reason
        enabled: page.app.choices.erase.possible
        selected: !page.app.alongside && page.app.partition === "" && page.app.choices.erase.possible
        onChosen: { page.app.alongside = false; page.app.partition = "" }
    }
}
