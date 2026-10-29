import QtQuick
import QtQuick.Shapes
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Hyprland
import "settings"

// Workspaces overview (feature 7, designs "Desktop C" and "Feat-Workspaces"):
// Super+Tab shows every workspace as a card around the core, with its windows
// drawn where they are. Click a card to go there, drag a window onto another
// card to move it, or open a card's details to name it and "remember" it:
// Core keeps the name and the apps that were open (/workspaces), and Restore
// relaunches those apps from their desktop entries onto that workspace with
// Hyprland's exec rules. Core never launches anything itself. Window titles
// come from apps, so they are plain text. The overview sits on the Top layer,
// below lock screens, and takes the keyboard only while it is open.
PanelWindow {
    id: ov
    property var theme
    property var bridge
    property var apps
    property bool reduced: false
    property bool open: false
    visible: open
    anchors { top: true; bottom: true; left: true; right: true }
    color: "transparent"
    exclusionMode: ExclusionMode.Ignore
    WlrLayershell.layer: WlrLayer.Top
    WlrLayershell.namespace: "nexus-overview"
    WlrLayershell.keyboardFocus: open ? WlrKeyboardFocus.Exclusive : WlrKeyboardFocus.None

    property int selected: 0                 // workspace id whose details are shown (0: none)
    property string selectedSaved: ""        // or a remembered workspace that is not open
    property var saved: []
    property string message: ""
    property int listId: -1
    property int saveId: -1
    property int forgetId: -1
    readonly property string filter: search.text.trim().toLowerCase()
    readonly property var monitor: Hyprland.focusedMonitor
    readonly property real monW: monitor && monitor.width > 0 ? monitor.width : 1920
    readonly property real monH: monitor && monitor.height > 0 ? monitor.height : 1080
    // Ids and names are checked again by Core; windows whose class does not fit are not remembered.
    readonly property var idPattern: /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/
    // (QML's JavaScript has no \p{L}: this keeps out the characters Core refuses; Core checks letters exactly.)
    readonly property var namePattern: /^[^\s\u0000-\u002f:-@\[-`{-\u00bf][^\u0000-\u001f!"#$%*+,\/:;<=>?@\[\\\]^`{|}~\u007f-\u00bf]{0,39}$/

    // ---- state ----
    readonly property var workspaces: Hyprland.workspaces.values.filter(w => w.id > 0).sort((a, b) => a.id - b.id)
    function windowsOf(ws) { return ws ? ws.toplevels.values : [] }
    function info(t) { return t.lastIpcObject || {} }
    function appIdOf(t) { return info(t).class || info(t).initialClass || "" }
    function workspaceById(id) { return workspaces.find(w => w.id === id) || null }
    function displayName(ws) { return ws.name && ws.name !== String(ws.id) ? ws.name : "" }
    function savedFor(ws) {
        const n = displayName(ws).toLowerCase();
        return saved.find(s => n ? s.name.toLowerCase() === n : false) || null;
    }
    // Remembered workspaces that are not open now.
    readonly property var closedSaved: saved.filter(s => !workspaces.some(w => displayName(w).toLowerCase() === s.name.toLowerCase()))
    readonly property var cards: workspaces.map(w => ({ ws: w, saved: null })).concat(closedSaved.map(s => ({ ws: null, saved: s })))
    function matches(card) {
        if (!filter) return true;
        const words = filter.replace(/^(open|show|restore)( my)? /, "");
        if (card.saved) return card.saved.name.toLowerCase().includes(words) || card.saved.apps.some(a => (a.title + " " + a.appId).toLowerCase().includes(words));
        return displayName(card.ws).toLowerCase().includes(words) ||
               windowsOf(card.ws).some(t => ((t.title || "") + " " + appIdOf(t)).toLowerCase().includes(words));
    }

    function show() {
        Hyprland.refreshWorkspaces();
        Hyprland.refreshToplevels();
        selected = 0; selectedSaved = ""; message = ""; search.text = "";
        open = true;
        refreshSaved();
        search.forceActiveFocus();
    }
    function hide() { open = false; selected = 0; selectedSaved = "" }
    function refreshSaved() { if (bridge.online) listId = bridge.api("GET", "/workspaces") }
    function switchTo(id) { Hyprland.dispatch("workspace " + id); hide() }
    function newWorkspace() {
        let id = 1;
        while (workspaces.some(w => w.id === id && windowsOf(w).length > 0)) id++;
        switchTo(id);
    }
    function address(t) { return t.address.startsWith("0x") ? t.address : "0x" + t.address }
    function focusWindow(t) { Hyprland.dispatch("focuswindow address:" + address(t)); hide() }
    function moveWindow(addr, id) {
        if (!/^(0x)?[0-9a-fA-F]+$/.test(addr) || !(id >= 1 && id <= 99)) return false;
        Hyprland.dispatch("movetoworkspacesilent " + id + ",address:" + (addr.startsWith("0x") ? addr : "0x" + addr));
        Hyprland.refreshToplevels();
        return true;
    }
    function rename(id, name) {
        name = name.trim();
        if (name && !namePattern.test(name)) { message = "Names can have letters, numbers, spaces and . - _ ' & ( )"; return false }
        Hyprland.dispatch("renameworkspace " + id + (name ? " " + name : ""));
        Hyprland.refreshWorkspaces();
        return true;
    }
    // Remember: the workspace's name and the apps on it, kept by Core.
    function remember(id, name) {
        const ws = workspaceById(id);
        name = (name || (ws ? displayName(ws) : "")).trim();
        if (!name) { message = "Give the workspace a name first."; return false }
        if (!namePattern.test(name)) { message = "Names can have letters, numbers, spaces and . - _ ' & ( )"; return false }
        if (!bridge.online) { message = "Nexus Core is not running, so workspaces cannot be remembered: " + bridge.offlineReason; return false }
        const wins = windowsOf(ws).filter(t => idPattern.test(appIdOf(t)));
        const list = wins.map(t => {
            const entry = DesktopEntries.heuristicLookup(appIdOf(t));
            return { appId: appIdOf(t), desktopId: entry && idPattern.test(entry.id) ? entry.id : null, title: t.title || "" };
        }).slice(0, 24);
        if (ws && displayName(ws) !== name) rename(id, name);
        message = "";
        saveId = bridge.api("POST", "/workspaces", { name: name, workspace: id, apps: list });
        return true;
    }
    function forget(name) { if (bridge.online) forgetId = bridge.api("DELETE", "/workspaces?name=" + encodeURIComponent(name)) }
    function quote(arg) { return "'" + String(arg).replace(/'/g, "'\\''") + "'" }
    // Restore: relaunch each remembered app that is not already there, onto its workspace.
    function restore(name) {
        const rec = saved.find(s => s.name.toLowerCase() === name.trim().toLowerCase());
        if (!rec) { message = "No remembered workspace is called “" + name + "”."; return false }
        const ws = workspaceById(rec.workspace);
        const there = windowsOf(ws).map(t => appIdOf(t).toLowerCase());
        const missing = [];
        let started = 0;
        for (const app of rec.apps) {
            if (there.includes(app.appId.toLowerCase())) continue;
            const entry = (app.desktopId && DesktopEntries.byId(app.desktopId)) || DesktopEntries.heuristicLookup(app.appId);
            const command = entry ? entry.command.filter(a => !/^%[a-zA-Z]$/.test(a)) : [];
            if (!command.length) { missing.push(app.appId); continue }
            Hyprland.dispatch("exec [workspace " + rec.workspace + " silent] " + command.map(quote).join(" "));
            started++;
        }
        Hyprland.dispatch("workspace " + rec.workspace);
        rename(rec.workspace, rec.name);
        message = (started ? "Opening " + started + (started === 1 ? " app" : " apps") + " on workspace " + rec.workspace + ". " : "") +
                  (missing.length ? "Could not find how to start: " + missing.join(", ") + "." : "");
        if (!missing.length) hide();
        return true;
    }

    Connections {
        target: ov.bridge
        function onResult(id, type, data) {
            if (id === ov.listId) ov.saved = data.workspaces || [];
            else if (id === ov.saveId) {
                ov.saved = data.workspaces || [];
                ov.message = "Remembered “" + data.workspace.name + "” with " + data.workspace.apps.length +
                             (data.workspace.apps.length === 1 ? " app." : " apps.") + " Say or type “open " + data.workspace.name + "” to bring it back.";
            } else if (id === ov.forgetId) { ov.saved = data.workspaces || []; ov.selectedSaved = ""; ov.message = "Forgotten. Your windows and files are not touched." }
        }
        function onFailed(id, message) { if ([ov.listId, ov.saveId, ov.forgetId].includes(id)) ov.message = message }
    }

    IpcHandler {
        target: "workspaces"
        function open(): void { ov.show() }
        function close(): void { ov.hide() }
        function toggle(): void { ov.open ? ov.hide() : ov.show() }
        function switchTo(id: int): void { ov.switchTo(id) }
        // Shows the details of an open workspace (by id) or a remembered one (by name).
        function select(id: int): void { if (!ov.open) ov.show(); ov.selectedSaved = ""; ov.selected = id }
        function selectSaved(name: string): void { if (!ov.open) ov.show(); ov.selected = 0; ov.selectedSaved = name }
        // Names and remembers the workspace whose details are open, else the focused one.
        function save(name: string): bool { return ov.remember(ov.selected || (Hyprland.focusedWorkspace ? Hyprland.focusedWorkspace.id : 1), name) }
        function restore(name: string): bool { return ov.restore(name) }
        function forget(name: string): void { ov.forget(name) }
        function rename(id: int, name: string): bool { return ov.rename(id, name) }
        function moveWindow(address: string, id: int): bool { return ov.moveWindow(address, id) }
        function state(): string {
            return JSON.stringify({ open: ov.open, selected: ov.selected, selectedSaved: ov.selectedSaved, message: ov.message,
                                    focused: Hyprland.focusedWorkspace ? Hyprland.focusedWorkspace.id : 0,
                                    workspaces: ov.workspaces.map(w => ({ id: w.id, name: ov.displayName(w),
                                        windows: ov.windowsOf(w).map(t => ({ address: ov.address(t), appId: ov.appIdOf(t), title: t.title })) })),
                                    remembered: ov.saved.map(s => ({ name: s.name, workspace: s.workspace, apps: s.apps.map(a => a.appId) })) })
        }
    }

    // ---- drawing ----
    readonly property bool detailOpen: selected > 0 || selectedSaved !== ""
    readonly property real cx: detailOpen ? width * 0.27 : width / 2
    readonly property real cy: height * 0.45
    readonly property real rx: detailOpen ? width * 0.17 : Math.min(width * 0.33, 560)
    readonly property real ry: Math.min(height * 0.27, 270)
    readonly property real cardW: detailOpen ? 210 : cards.length > 6 ? 200 : 240
    readonly property real cardH: cardW * 0.7

    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: Qt.rgba(0.06, 0.08, 0.23, 0.97) }
            GradientStop { position: 0.6; color: Qt.rgba(0.02, 0.03, 0.10, 0.97) }
            GradientStop { position: 1; color: Qt.rgba(0.01, 0.01, 0.04, 0.98) }
        }
        // A click on empty space closes the details, then the overview.
        TapHandler { onTapped: ov.detailOpen ? (ov.selected = 0, ov.selectedSaved = "") : ov.hide() }
    }
    Canvas {
        anchors.fill: parent
        onWidthChanged: requestPaint()
        onPaint: {
            const ctx = getContext("2d");
            ctx.reset();
            let seed = 23;
            const rnd = () => { seed = (seed * 1664525 + 1013904223) % 4294967296; return seed / 4294967296 };
            for (let i = 0; i < 90; i++) {
                ctx.fillStyle = "rgba(201,212,255," + (0.25 + rnd() * 0.6).toFixed(2) + ")";
                const s = rnd() < 0.85 ? 1 : 2;
                ctx.fillRect(Math.round(rnd() * width), Math.round(rnd() * height), s, s);
            }
        }
    }
    Text { anchors.horizontalCenter: parent.horizontalCenter; y: 64; text: "Workspaces · Super + Tab"; color: ov.theme.inkSecondary; font.pixelSize: 13 }

    // Orbit rings and the core.
    Shape {
        x: ov.cx; y: ov.cy
        preferredRendererType: Shape.CurveRenderer
        Behavior on x { NumberAnimation { duration: ov.reduced ? 0 : 220; easing.type: Easing.OutCubic } }
        ShapePath {
            strokeColor: Qt.rgba(0.55, 0.62, 1.0, 0.2); strokeWidth: 1; fillColor: "transparent"
            PathAngleArc { radiusX: ov.rx + 40; radiusY: ov.ry; sweepAngle: 360 }
        }
        ShapePath {
            strokeColor: Qt.rgba(0.55, 0.62, 1.0, 0.16); strokeWidth: 1; fillColor: "transparent"
            strokeStyle: ShapePath.DashLine; dashPattern: [3, 6]
            PathAngleArc { radiusX: (ov.rx + 40) * 0.55; radiusY: ov.ry * 0.5; sweepAngle: 360 }
        }
        ShapePath {
            strokeColor: "transparent"
            fillGradient: RadialGradient {
                centerX: 0; centerY: 0; centerRadius: 100; focalX: 0; focalY: 0
                GradientStop { position: 0; color: "#FFFFFF" }
                GradientStop { position: 0.05; color: "#FFE3B8" }
                GradientStop { position: 0.15; color: "#FFA65C" }
                GradientStop { position: 0.34; color: Qt.rgba(0.84, 0.41, 0.25, 0.92) }
                GradientStop { position: 0.55; color: Qt.rgba(0.55, 0.31, 0.78, 0.6) }
                GradientStop { position: 0.7; color: Qt.rgba(0.49, 0.42, 1.0, 0.14) }
                GradientStop { position: 0.76; color: Qt.rgba(0.49, 0.42, 1.0, 0) }
            }
            PathAngleArc { radiusX: 100; radiusY: 100; sweepAngle: 360 }
        }
    }
    Column {
        x: ov.cx - width / 2; y: ov.cy + 84; spacing: 2
        Text { anchors.horizontalCenter: parent.horizontalCenter; text: "Nexus"; color: ov.theme.ink; font.pixelSize: 17; font.weight: 500 }
        Text {
            anchors.horizontalCenter: parent.horizontalCenter; color: ov.theme.inkSecondary; font.pixelSize: 12
            text: {
                const n = ov.workspaces.length, w = ov.workspaces.reduce((a, ws) => a + ov.windowsOf(ws).length, 0);
                return n + (n === 1 ? " workspace · " : " workspaces · ") + w + (w === 1 ? " window" : " windows");
            }
        }
    }

    // A window being dragged to another workspace.
    Rectangle {
        id: ghost
        property string addr: ""
        property string label: ""
        visible: addr !== ""
        z: 10; width: 150; height: 40; radius: 8
        color: "#18234A"; border.color: ov.theme.focus
        Text { anchors.centerIn: parent; width: parent.width - 16; elide: Text.ElideRight; horizontalAlignment: Text.AlignHCenter
               textFormat: Text.PlainText; text: ghost.label; color: ov.theme.ink; font.pixelSize: 12 }
    }
    function cardAt(px, py) {
        for (let i = 0; i < cardRepeater.count; i++) {
            const c = cardRepeater.itemAt(i);
            if (c && c.card.ws && px >= c.x && px <= c.x + c.width && py >= c.y && py <= c.y + c.height) return c.card.ws.id;
        }
        return 0;
    }

    Repeater {
        id: cardRepeater
        model: ov.cards
        delegate: Rectangle {
            id: cardItem
            required property var modelData
            required property int index
            readonly property var card: modelData
            readonly property var ws: modelData.ws
            readonly property bool isSelected: ws ? ov.selected === ws.id : ov.selectedSaved === modelData.saved.name
            readonly property bool focusedWs: !!ws && !!Hyprland.focusedWorkspace && Hyprland.focusedWorkspace.id === ws.id
            // Left and right of the core in rows, as in the design; one card sits above it.
            readonly property int rows: Math.ceil(ov.cards.length / 2)
            readonly property real tilt: rows <= 1 ? 0 : (-58 + 116 * Math.floor(index / 2) / (rows - 1)) * Math.PI / 180
            readonly property real angle: ov.cards.length === 1 ? -Math.PI / 2 : index % 2 === 0 ? Math.PI - tilt : tilt
            readonly property bool dropTarget: ghost.visible && !!ws && ov.cardAt(ghost.x + ghost.width / 2, ghost.y + ghost.height / 2) === ws.id
            x: ov.cx + ov.rx * Math.cos(angle) - width / 2
            y: Math.max(96, Math.min(ov.height - 200 - height, ov.cy + ov.ry * Math.sin(angle) - height / 2))
            Behavior on x { NumberAnimation { duration: ov.reduced ? 0 : 220; easing.type: Easing.OutCubic } }
            width: ov.cardW; height: ov.cardH
            radius: 16
            opacity: ov.matches(modelData) ? 1 : 0.3
            color: Qt.rgba(0.04, 0.07, 0.15, 0.9)
            // The border is drawn on top of the preview (below).
            activeFocusOnTab: true
            Keys.onReturnPressed: ws ? ov.switchTo(ws.id) : ov.selectedSaved = modelData.saved.name
            Keys.onSpacePressed: ws ? (ov.selected = ws.id, ov.selectedSaved = "") : ov.selectedSaved = modelData.saved.name
            Accessible.role: Accessible.Button
            Accessible.name: ws ? "Workspace " + ws.id + (ov.displayName(ws) ? ", " + ov.displayName(ws) : "") + ", " + ov.windowsOf(ws).length + " windows"
                                : "Remembered workspace " + modelData.saved.name

            // The windows, where they are on the screen.
            Rectangle {
                id: preview
                x: 0; y: 0; width: parent.width; height: parent.height - 44
                color: "#070B1C"; radius: 16
                Rectangle { anchors { left: parent.left; right: parent.right; bottom: parent.bottom } height: 16; color: "#070B1C" }
                Rectangle {
                    visible: !cardItem.ws
                    anchors.fill: parent; anchors.margins: 10; radius: 8; color: "transparent"
                    border.color: "#3A4C80"
                    Text { anchors.centerIn: parent; width: parent.width - 16; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
                           text: cardItem.ws ? "" : "Remembered · " + cardItem.modelData.saved.apps.length + " apps"
                           color: ov.theme.inkSecondary; font.pixelSize: 12 }
                }
                Text { visible: !!cardItem.ws && ov.windowsOf(cardItem.ws).length === 0; anchors.centerIn: parent
                       text: "Empty"; color: ov.theme.muted; font.pixelSize: 12 }
                Repeater {
                    model: cardItem.ws ? ov.windowsOf(cardItem.ws) : []
                    delegate: Rectangle {
                        id: mini
                        required property var modelData
                        readonly property var o: ov.info(modelData)
                        readonly property real sx: (preview.width - 16) / ov.monW
                        readonly property real sy: (preview.height - 16) / ov.monH
                        readonly property var at: o.at || [0, 0]
                        readonly property var size: o.size || [ov.monW / 2, ov.monH / 2]
                        x: 8 + Math.max(0, (at[0] - (ov.monitor ? ov.monitor.x : 0)) * sx)
                        y: 8 + Math.max(0, (at[1] - (ov.monitor ? ov.monitor.y : 0)) * sy)
                        width: Math.max(18, Math.min(preview.width - 16, size[0] * sx))
                        height: Math.max(14, Math.min(preview.height - 16, size[1] * sy))
                        radius: 6
                        color: miniHover.hovered ? "#23305E" : "#18234A"
                        border.color: modelData.activated ? ov.theme.focus : ov.theme.glassBorder
                        Image {
                            anchors.centerIn: parent
                            readonly property real s: Math.min(22, parent.width - 6, parent.height - 6)
                            width: s; height: s; sourceSize: Qt.size(44, 44)
                            visible: s >= 10 && source != ""
                            source: ov.apps ? ov.apps.iconFor(ov.appIdOf(mini.modelData)) : ""
                        }
                        HoverHandler { id: miniHover; cursorShape: Qt.OpenHandCursor }
                        TapHandler { onTapped: ov.focusWindow(mini.modelData) }
                        DragHandler {
                            target: null
                            onActiveChanged: {
                                if (active) {
                                    ghost.addr = ov.address(mini.modelData);
                                    ghost.label = mini.modelData.title || ov.appIdOf(mini.modelData);
                                } else {
                                    const id = ov.cardAt(ghost.x + ghost.width / 2, ghost.y + ghost.height / 2);
                                    if (id && cardItem.ws && id !== cardItem.ws.id) ov.moveWindow(ghost.addr, id);
                                    ghost.addr = "";
                                }
                            }
                            onCentroidChanged: {
                                if (!active) return;
                                const p = mini.mapToItem(null, centroid.position.x, centroid.position.y);
                                ghost.x = p.x - ghost.width / 2; ghost.y = p.y - ghost.height / 2;
                            }
                        }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Window " + (modelData.title || ov.appIdOf(modelData))
                    }
                }
            }
            Rectangle {
                anchors.fill: parent; radius: 16; color: "transparent"
                border.color: cardItem.isSelected || cardItem.dropTarget || cardItem.activeFocus || cardHover.hovered ? "#86A8FF"
                            : cardItem.focusedWs ? "#5F6FB0" : ov.theme.glassBorder
                border.width: cardItem.dropTarget || cardItem.activeFocus ? 2 : 1
            }
            Text {
                x: 14; y: preview.height + (44 - height) / 2; width: parent.width - 28 - count.width - 34
                elide: Text.ElideRight; textFormat: Text.PlainText
                text: cardItem.ws ? cardItem.ws.id + " · " + (ov.displayName(cardItem.ws) || (cardItem.focusedWs ? "This workspace" : "Workspace"))
                                  : cardItem.modelData.saved.name
                color: ov.theme.ink; font.pixelSize: 13; font.weight: 500
            }
            Text {
                id: count
                anchors { right: details.left; rightMargin: 6 }
                y: preview.height + (44 - height) / 2
                text: cardItem.ws ? ov.windowsOf(cardItem.ws).length : ""
                color: ov.theme.inkSecondary; font.pixelSize: 12
            }
            HoverHandler { id: cardHover; cursorShape: Qt.PointingHandCursor }
            TapHandler {
                onTapped: cardItem.ws ? ov.switchTo(cardItem.ws.id) : (ov.selected = 0, ov.selectedSaved = cardItem.modelData.saved.name)
            }
            // Details: name, remember, restore.
            Rectangle {
                id: details
                anchors { right: parent.right; rightMargin: 8 }
                y: preview.height + 7; width: 30; height: 30; radius: 9
                color: detailsHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.25) : "#121B33"
                border.color: ov.theme.glassBorder
                Text { anchors.centerIn: parent; text: "⋯"; color: ov.theme.inkSecondary; font.pixelSize: 15 }
                HoverHandler { id: detailsHover; cursorShape: Qt.PointingHandCursor }
                TapHandler {
                    onTapped: {
                        if (cardItem.ws) { ov.selectedSaved = ""; ov.selected = cardItem.ws.id }
                        else { ov.selected = 0; ov.selectedSaved = cardItem.modelData.saved.name }
                    }
                }
                Accessible.role: Accessible.Button
                Accessible.name: "Details, name and remember"
            }
        }
    }

    // + New workspace
    Rectangle {
        visible: !ov.detailOpen
        x: ov.cx + ov.rx - width / 2 + 60; y: Math.min(ov.height - 190, ov.cy + ov.ry + 40)
        width: newLabel.implicitWidth + 32; height: 40; radius: 12
        color: newHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.18) : "transparent"
        border.color: "#3A4C80"
        Text { id: newLabel; anchors.centerIn: parent; text: "+ New workspace"; color: ov.theme.inkSecondary; font.pixelSize: 13 }
        HoverHandler { id: newHover; cursorShape: Qt.PointingHandCursor }
        TapHandler { onTapped: ov.newWorkspace() }
        Accessible.role: Accessible.Button
        Accessible.name: "New workspace"
    }

    // ---- details of one workspace ----
    Rectangle {
        id: panel
        readonly property var ws: ov.selected ? ov.workspaceById(ov.selected) : null
        readonly property var rec: ws ? ov.savedFor(ws) : ov.saved.find(s => s.name === ov.selectedSaved) || null
        visible: ov.detailOpen
        x: ov.width * 0.5; y: 96; width: Math.min(620, ov.width * 0.46)
        height: Math.min(detailColumn.implicitHeight + 100, ov.height - 260)
        radius: 22; color: Qt.rgba(0.04, 0.07, 0.15, 0.96); border.color: "#86A8FF"
        TapHandler {}   // keeps clicks inside the panel from closing it
        onWsChanged: nameField.text = ws ? ov.displayName(ws) : ""
        onVisibleChanged: nameField.text = ws ? ov.displayName(ws) : ""
        Column {
            id: detailColumn
            x: 24; y: 22; width: parent.width - 48; spacing: 14
            Text {
                color: ov.theme.inkSecondary; font.pixelSize: 12; font.letterSpacing: 0.5
                text: (panel.ws ? "WORKSPACE " + panel.ws.id : panel.rec ? "WORKSPACE " + panel.rec.workspace + " · NOT OPEN" : "") +
                      (panel.rec ? " · REMEMBERED " + new Date(panel.rec.savedAt).toLocaleString(Qt.locale(), "d MMM HH:mm").toUpperCase()
                                 : " · NOT REMEMBERED YET")
            }
            // The name: type to rename (Enter).
            Rectangle {
                width: parent.width; height: 46; radius: 12; color: "#0E1630"
                border.color: nameField.activeFocus ? ov.theme.focus : "#1F2C55"
                TextInput {
                    id: nameField
                    x: 14; width: parent.width - 28; anchors.verticalCenter: parent.verticalCenter
                    readOnly: !panel.ws
                    text: ""; color: ov.theme.ink; font.pixelSize: 20; font.weight: 600; clip: true; maximumLength: 40
                    Text { visible: !nameField.text; text: panel.rec ? panel.rec.name : "Name this workspace, e.g. Dissertation"
                           color: panel.rec ? ov.theme.ink : ov.theme.muted; font.pixelSize: 20; font.weight: panel.rec ? 600 : 400 }
                    onAccepted: if (panel.ws && ov.rename(panel.ws.id, text)) ov.message = text.trim() ? "Named “" + text.trim() + "”." : "Name removed."
                    Keys.onEscapePressed: ov.selected = 0
                    Accessible.role: Accessible.EditableText
                    Accessible.name: "Workspace name"
                }
            }
            Text {
                text: panel.ws ? "Windows · " + ov.windowsOf(panel.ws).length : panel.rec ? "Apps it remembers · " + panel.rec.apps.length : ""
                color: ov.theme.inkSecondary; font.pixelSize: 12
            }
            Grid {
                width: parent.width; columns: 4; spacing: 8
                Repeater {
                    model: panel.ws ? ov.windowsOf(panel.ws).map(t => ({ appId: ov.appIdOf(t), title: t.title || "" }))
                         : panel.rec ? panel.rec.apps : []
                    delegate: Rectangle {
                        required property var modelData
                        width: (panel.width - 48 - 24) / 4; height: 92; radius: 10; color: "#0E1630"; border.color: "#1F2C55"
                        Rectangle {
                            width: parent.width - 2; x: 1; y: 1; height: 48; radius: 9; color: "#070B1C"
                            Image { anchors.centerIn: parent; width: 26; height: 26; sourceSize: Qt.size(52, 52)
                                    source: ov.apps ? ov.apps.iconFor(modelData.appId) : "" }
                        }
                        Column {
                            x: 9; y: 54; width: parent.width - 18; spacing: 1
                            Text { width: parent.width; elide: Text.ElideRight; textFormat: Text.PlainText
                                   text: modelData.appId; color: ov.theme.ink; font.pixelSize: 12; font.weight: 500 }
                            Text { width: parent.width; elide: Text.ElideRight; textFormat: Text.PlainText
                                   text: modelData.title; color: ov.theme.inkSecondary; font.pixelSize: 11 }
                        }
                    }
                }
            }
            Text {
                width: parent.width; wrapMode: Text.Wrap; color: ov.theme.inkSecondary; font.pixelSize: 12; lineHeight: 1.3
                text: "Remember keeps this workspace's name and the apps open on it, on this computer. Restore reopens those apps " +
                      "and moves them back here. Open files and browser tabs are not kept yet."
            }
            Text { visible: ov.message !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                   text: ov.message; color: "#C9D4FF"; font.pixelSize: 13 }
        }
        Row {
            anchors { right: parent.right; rightMargin: 24; bottom: parent.bottom; bottomMargin: 20 }
            spacing: 10
            Button { theme: ov.theme; visible: !!panel.rec; label: "Forget workspace"; onClicked: ov.forget(panel.rec.name) }
            Button { theme: ov.theme; visible: !!panel.ws; label: panel.rec ? "Remember again" : "Remember"; primary: !panel.rec
                     onClicked: ov.remember(panel.ws.id, nameField.text) }
            Button { theme: ov.theme; visible: !!panel.rec; label: "Restore"; primary: true; onClicked: ov.restore(panel.rec.name) }
            Button { theme: ov.theme; visible: !!panel.ws; label: "Go there"; onClicked: ov.switchTo(panel.ws.id) }
        }
    }

    // Search windows and workspaces; Enter goes to the first match.
    Rectangle {
        x: ov.detailOpen ? 60 : (ov.width - width) / 2; y: ov.height - 150
        width: ov.detailOpen ? Math.min(560, ov.width * 0.44) : 600; height: 56; radius: 28
        color: Qt.rgba(0.04, 0.07, 0.15, 0.92); border.color: search.activeFocus ? Qt.rgba(0.53, 0.66, 1.0, 0.55) : ov.theme.glassBorder
        Image { x: 20; anchors.verticalCenter: parent.verticalCenter; width: 18; height: 18; sourceSize: Qt.size(36, 36); source: "icons/search.svg" }
        TextInput {
            id: search
            x: 50; width: parent.width - 70; anchors.verticalCenter: parent.verticalCenter
            color: ov.theme.ink; font.pixelSize: 15; clip: true
            Text { visible: !search.text; text: "Search windows and workspaces, e.g. open my dissertation"; color: ov.theme.inkSecondary; font.pixelSize: 15 }
            Keys.onEscapePressed: { if (text) text = ""; else if (ov.detailOpen) { ov.selected = 0; ov.selectedSaved = "" } else ov.hide() }
            onAccepted: {
                const first = ov.cards.find(c => ov.matches(c));
                if (!first) return;
                if (first.saved) ov.restore(first.saved.name);
                else {
                    const words = ov.filter.replace(/^(open|show|restore)( my)? /, "");
                    const t = ov.windowsOf(first.ws).find(t => ((t.title || "") + " " + ov.appIdOf(t)).toLowerCase().includes(words));
                    t ? ov.focusWindow(t) : ov.switchTo(first.ws.id);
                }
            }
            Accessible.role: Accessible.EditableText
            Accessible.name: "Search windows and workspaces"
        }
    }
}
