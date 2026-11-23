import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/help.mjs" as H

// Settings, Help: the user guide (docs/USER-GUIDE.md, copied into the shell
// by shell/tools/build.py), as topics you can search. "Open …" links go to the
// Settings page they name; web links open in the browser; nothing else is
// followed. Ask Nexus is there for anything the guide does not cover.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    property var all: []
    property string query: ""
    property string shown: ""
    readonly property var found: H.search(all, query)
    readonly property var pageIds: host.pages.map(p => p.id)

    FileView { path: Quickshell.shellDir + "/help/user-guide.md"; onLoaded: page.all = H.topics(text()) }
    function follow(href) {
        const l = H.link(href, pageIds);
        if (!l) return;
        if (l.url) Quickshell.execDetached(["xdg-open", l.url]);
        else page.host.open(l.page, l.highlight);
    }

    PageHeader { theme: page.theme; title: "Help"; summary: "How to do things in NexusOS. Or ask Nexus, in your own words." }
    Row {
        spacing: 8
        TextField { id: searchField; theme: page.theme; label: "Search the guide"; placeholder: "backups, printer, password…"
                    width: Math.min(360, page.width - 140); onTextChanged: { page.query = text; page.shown = "" } }
        Button { anchors.bottom: parent.bottom; theme: page.theme; label: "Ask Nexus"
                 onClicked: page.host.askNexus(searchField.text.trim() || "How do I ") }
    }
    Text {
        visible: page.all.length > 0 && page.found.length === 0
        width: page.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        text: "Nothing in the guide about that. Ask Nexus: it can look at this computer too."
    }
    Repeater {
        model: page.found
        delegate: Rectangle {
            required property var modelData
            readonly property bool open: page.shown === modelData.title || (page.query !== "" && page.found.length === 1)
            width: page.width; radius: 10
            height: open ? body.y + body.implicitHeight + 14 : 44
            color: page.theme.chip
            Text {
                x: 16; y: 12; text: modelData.title; color: page.theme.ink; font.pixelSize: 14; font.weight: 500
            }
            TapHandler { onTapped: page.shown = open ? "" : modelData.title }
            Text {
                id: body
                visible: open
                x: 16; y: 40; width: parent.width - 32
                wrapMode: Text.Wrap; textFormat: Text.MarkdownText
                text: modelData.body; color: page.theme.inkSecondary; linkColor: page.theme.focus; font.pixelSize: 13
                onLinkActivated: href => page.follow(href)
                HoverHandler { cursorShape: body.hoveredLink ? Qt.PointingHandCursor : Qt.ArrowCursor }
            }
        }
    }
}
