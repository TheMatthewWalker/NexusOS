import QtQuick
import Quickshell
import Quickshell.Io
import "settings"

// Private search (feature 8, design "Feat-Search"): one box that searches the
// folders the person chose, through Core's local index (POST /search). Each
// result says where it was found and why it matched, from what the index
// actually matched, never a summary. Nothing leaves the computer, and nothing
// outside the chosen folders is searched; Settings, Search chooses them.
// File contents are untrusted text, so snippets are plain text. Opened by
// Super+S, or `nexus-shell-ipc call search query "<words>"`.
FloatingWindow {
    id: win
    property var theme
    property var bridge
    signal chooseFolders()
    title: "Search"
    visible: false
    implicitWidth: 860; implicitHeight: 660
    minimumSize: Qt.size(640, 480)
    color: "#05071A"

    property string kind: ""            // "" (all) or one of Core's kinds
    readonly property var kinds: [
        { value: "", label: "All" }, { value: "document", label: "Documents" }, { value: "text", label: "Notes and text" },
        { value: "data", label: "Spreadsheets and data" }, { value: "code", label: "Code" }, { value: "mail", label: "Mail" }
    ]
    property var response: null
    property var status: null
    property bool searching: false
    property string message: ""
    property string lastQuery: ""
    property int searchId: -1
    property int statusId: -1
    readonly property var results: response ? response.results : []
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    readonly property string home: Quickshell.env("HOME") || ""

    function open() {
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Search)$"]);
        visible = true;
        field.forceActiveFocus();
        refreshStatus();
    }
    function refreshStatus() { if (bridge.online) statusId = bridge.api("GET", "/search/status") }
    function search(text) {
        const q = (text || "").trim();
        if (!q) { response = null; message = ""; return }
        if (!bridge.online) { message = "Nexus Core is not running, so search is not available: " + bridge.offlineReason; return }
        lastQuery = q; searching = true; message = "";
        searchId = bridge.api("POST", "/search", { query: q.slice(0, 500), limit: 20, kinds: kind ? [kind] : null });
    }
    // Opens with the person's default app for that kind of file (xdg-open).
    function openResult(i) {
        const r = results[i];
        if (!r) return false;
        Quickshell.execDetached(["xdg-open", r.path]);
        return true;
    }
    function showInFolder(i) {
        const r = results[i];
        if (r) Quickshell.execDetached(["xdg-open", r.path.slice(0, r.path.lastIndexOf("/")) || "/"]);
    }
    // "Documents › Work · edited 28 Mar · lines 3–9"
    function where(r) {
        let dir = r.path.slice(0, r.path.lastIndexOf("/"));
        if (home && (dir === home || dir.startsWith(home + "/"))) dir = dir.slice(home.length + 1);
        const folder = dir ? dir.split("/").join(" › ") : "Home";
        const d = new Date(r.modified);
        return folder + " · edited " + d.toLocaleDateString(Qt.locale(), "d MMM yyyy");
    }
    function badge(r) {
        const dot = r.path.lastIndexOf(".");
        const ext = dot > r.path.lastIndexOf("/") ? r.path.slice(dot + 1) : "";
        return (ext || r.kind).slice(0, 4).toUpperCase();
    }

    Connections {
        target: win.bridge
        function onResult(id, type, data) {
            if (id === win.searchId) {
                win.searching = false; win.response = data;
                if (!data.results.length) win.message = "Nothing in the searched folders matches “" + win.lastQuery + "”.";
            } else if (id === win.statusId) win.status = data;
        }
        function onFailed(id, message) {
            if (id === win.searchId) { win.searching = false; win.message = "Search did not work: " + message }
        }
    }
    Timer { id: typing; interval: 450; onTriggered: win.search(field.text) }
    onKindChanged: if (field.text.trim()) search(field.text)

    IpcHandler {
        target: "search"
        function open(): void { win.open() }
        function close(): void { win.visible = false }
        // Types the words and searches, as a person would.
        function query(text: string): void { win.open(); field.text = text; win.search(text) }
        function filter(kind: string): void { if (win.kinds.some(k => k.value === kind)) win.kind = kind }
        // Opens result number i (0 is the top one) in its default app.
        function openResult(i: int): bool { return win.openResult(i) }
        // Adds a folder to what is searched, as Settings, Search does (Core still refuses hidden and key folders).
        function addFolder(path: string): void { if (win.bridge.online) win.bridge.api("POST", "/search/sources", { paths: [path] }) }
        function state(): string {
            return JSON.stringify({ visible: win.visible, query: win.lastQuery, kind: win.kind, searching: win.searching,
                                    message: win.message, notice: win.response ? win.response.notice : null,
                                    sources: win.status ? win.status.sources.map(s => s.path) : [],
                                    results: win.results.map(r => ({ path: r.path, title: r.title, why: r.why })) })
        }
    }

    // ---- the window ----
    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#10153A" }
            GradientStop { position: 0.55; color: "#05071A" }
            GradientStop { position: 1; color: "#020309" }
        }
    }
    Column {
        id: top
        x: 24; y: 22; width: parent.width - 48
        spacing: 12
        Rectangle {
            width: parent.width; height: 58; radius: 18
            color: Qt.rgba(0.04, 0.07, 0.15, 0.95)
            border.color: field.activeFocus ? Qt.rgba(0.53, 0.66, 1.0, 0.55) : win.theme.glassBorder
            Image { x: 20; anchors.verticalCenter: parent.verticalCenter; width: 18; height: 18
                    sourceSize: Qt.size(36, 36); source: "icons/search.svg" }
            TextInput {
                id: field
                x: 52; width: parent.width - 72; anchors.verticalCenter: parent.verticalCenter
                color: win.theme.ink; font.pixelSize: 17; clip: true
                Text { visible: !field.text; color: win.theme.inkSecondary; font.pixelSize: 17
                       text: "Search your files, e.g. the spreadsheet with supplier prices" }
                onTextEdited: typing.restart()
                onAccepted: { typing.stop(); win.search(text) }
                Keys.onEscapePressed: { if (text) text = ""; else win.visible = false }
                Keys.onDownPressed: list.forceActiveFocus()
                Accessible.role: Accessible.EditableText
                Accessible.name: "Search everything"
            }
        }
        // Filters, as pills.
        Flow {
            width: parent.width; spacing: 6
            Repeater {
                model: win.kinds
                delegate: Rectangle {
                    required property var modelData
                    readonly property bool on: win.kind === modelData.value
                    width: pill.implicitWidth + 24; height: 28; radius: 14
                    color: on ? "#5F4FE0" : pillHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.2) : "#0E1630"
                    border.color: on ? "transparent" : activeFocus ? win.theme.focus : win.theme.glassBorder
                    activeFocusOnTab: true
                    Keys.onSpacePressed: win.kind = modelData.value
                    Keys.onReturnPressed: win.kind = modelData.value
                    Text { id: pill; anchors.centerIn: parent; text: modelData.label; color: on ? "#FFFFFF" : win.theme.ink; font.pixelSize: 12 }
                    HoverHandler { id: pillHover; cursorShape: Qt.PointingHandCursor }
                    TapHandler { onTapped: win.kind = modelData.value }
                    Accessible.role: Accessible.RadioButton
                    Accessible.checked: on
                    Accessible.name: "Show " + modelData.label
                }
            }
        }
        Text {
            visible: text !== ""
            width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
            text: win.searching ? "Searching…" : win.message || (win.response && win.response.notice ? win.response.notice : "")
            color: win.theme.inkSecondary; font.pixelSize: 13
        }
    }

    // Nothing chosen yet: say so, and offer to choose.
    Column {
        visible: !!win.status && win.status.sources.length === 0
        anchors.centerIn: parent; width: Math.min(460, parent.width - 80); spacing: 12
        Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
               text: "No folders are searched yet"; color: win.theme.ink; font.pixelSize: 17; font.weight: 600 }
        Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
               text: "Choose which folders Nexus may search, such as Documents. Nothing outside them is read, and the index stays on this computer."
               color: win.theme.inkSecondary; font.pixelSize: 13 }
        Button { anchors.horizontalCenter: parent.horizontalCenter; theme: win.theme; primary: true; label: "Choose folders"
                 onClicked: win.chooseFolders() }
    }

    ListView {
        id: list
        x: 12; y: top.y + top.height + 10; width: parent.width - 24
        height: parent.height - y - footer.height - 8
        clip: true; spacing: 6
        model: win.results
        keyNavigationEnabled: true
        Keys.onReturnPressed: win.openResult(currentIndex)
        Keys.onUpPressed: { if (currentIndex <= 0) field.forceActiveFocus(); else decrementCurrentIndex() }
        delegate: Rectangle {
            id: hit
            required property var modelData
            required property int index
            readonly property bool current: ListView.isCurrentItem && list.activeFocus
            width: list.width; height: texts.implicitHeight + 28; radius: 14
            color: index === 0 || current || hitHover.hovered ? "#0E1630" : "transparent"
            border.color: current ? win.theme.focus : index === 0 ? "#86A8FF" : "transparent"
            Rectangle {
                x: 14; y: 14; width: 40; height: 40; radius: 10; color: "#121B33"; border.color: win.theme.glassBorder
                Text { anchors.centerIn: parent; text: win.badge(hit.modelData); color: win.theme.inkSecondary; font.pixelSize: 10; font.weight: 600 }
            }
            Column {
                id: texts
                x: 68; y: 14; width: parent.width - 68 - buttons.width - 28
                spacing: 3
                Text { width: parent.width; elide: Text.ElideMiddle; textFormat: Text.PlainText
                       text: hit.modelData.title || hit.modelData.path.split("/").pop(); color: win.theme.ink; font.pixelSize: 14; font.weight: 500 }
                Text { width: parent.width; elide: Text.ElideMiddle; textFormat: Text.PlainText
                       text: win.where(hit.modelData); color: win.theme.inkSecondary; font.pixelSize: 12 }
                Text { width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; topPadding: 2
                       text: "Why: " + hit.modelData.why; color: "#C9D4FF"; font.pixelSize: 12 }
                Text { width: parent.width; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
                       textFormat: Text.PlainText; topPadding: 2
                       text: hit.modelData.snippet; color: win.theme.muted; font.pixelSize: 12 }
            }
            Row {
                id: buttons
                anchors { right: parent.right; rightMargin: 14; verticalCenter: parent.verticalCenter }
                spacing: 6
                Button { theme: win.theme; label: "Folder"; onClicked: win.showInFolder(hit.index) }
                Button { theme: win.theme; label: "Open"; primary: hit.index === 0; onClicked: win.openResult(hit.index) }
            }
            HoverHandler { id: hitHover }
            TapHandler { onDoubleTapped: win.openResult(hit.index) }
            Accessible.role: Accessible.ListItem
            Accessible.name: (hit.modelData.title || hit.modelData.path) + ". " + hit.modelData.why
        }
    }

    // Where the search ran.
    Rectangle {
        id: footer
        anchors { left: parent.left; right: parent.right; bottom: parent.bottom }
        height: 48; color: "transparent"
        Rectangle { width: parent.width; height: 1; color: "#1B2647" }
        Rectangle { x: 24; anchors.verticalCenter: parent.verticalCenter; width: 8; height: 8; radius: 4; color: "#7FE0B0" }
        Text {
            x: 42; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 260; elide: Text.ElideRight
            color: win.theme.inkSecondary; font.pixelSize: 12
            text: {
                const s = win.status;
                if (!s) return "Searched on this computer only";
                const n = s.sources.length;
                return "Searched on this computer only · " + n + (n === 1 ? " folder · " : " folders · ") + s.files + " files" +
                       (s.running ? " · indexing…" : "");
            }
        }
        Text {
            anchors { right: parent.right; rightMargin: 24; verticalCenter: parent.verticalCenter }
            text: "Choose what is searched"; color: linkHover.hovered ? "#B7CAFF" : "#86A8FF"; font.pixelSize: 12
            HoverHandler { id: linkHover; cursorShape: Qt.PointingHandCursor }
            TapHandler { onTapped: win.chooseFolders() }
            Accessible.role: Accessible.Link
            Accessible.name: "Choose what is searched"
        }
    }
}
