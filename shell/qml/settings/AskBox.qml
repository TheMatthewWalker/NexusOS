import QtQuick
import ".."

// Ask in Settings (design: "Settings C · Ask"): the person says what they want
// ("make text bigger", "turn off Bluetooth") and Nexus answers here. The
// matching settings come from Settings' own search index at once and the top
// one is highlighted on its page; the question then goes to Core's ordinary
// chat (bridge.chat, the same path as the Nexus orb, no separate model call),
// and what Nexus offers comes back as the usual action cards: a personal
// setting is a "Do it" card, run only on the person's click through Core's
// /actions endpoint; a system change goes to Guardian. A Settings page Nexus
// points to (open_settings or change_setting) is highlighted too.
Rectangle {
    id: box
    property var theme
    property var bridge
    property var host            // the Settings window: search(), open(), highlight
    property var cardHost: null  // the shell's ActionCards: the windows a "Do it" may need

    property bool shown: false
    property string question: ""
    property string reply: ""
    property string error: ""
    property bool thinking: false
    property int chatId: -1
    readonly property var matches: question ? host.search(question) : []

    // Where a Nexus action points in Settings: [page, highlight].
    readonly property var settingPlaces: ({ volume: ["sound", "volume"], night_light: ["display", "nightlight"],
        scale: ["display", "scale"], windows: ["desktop", "windows"], blur: ["desktop", "blur"],
        animations: ["desktop", "animations"], high_contrast: ["accessibility", "contrast"] })

    function show(text) {
        shown = true;
        askField.text = text || "";
        askField.forceActiveFocus();
        if (text && text.trim()) ask(text);
    }
    function close() {
        shown = false; question = ""; reply = ""; error = ""; thinking = false; chatId = -1;
        cards.clear(); host.highlight = "";
    }
    // Stays lit while the answer is open (Settings' own highlight fades after a moment).
    function point(page, item) {
        host.open(page, "");
        host.highlight = item || "";
    }
    function ask(text) {
        const q = (text || "").trim();
        if (!q) return;
        question = q; reply = ""; error = ""; cards.clear();
        const m = host.search(q);
        if (m.length) point(m[0].page, m[0].highlight);
        if (!bridge || !bridge.online) { error = "Nexus Core is offline, so only the matching settings are shown."; return }
        thinking = true;
        chatId = bridge.chat("From Settings: " + q);
    }
    // An action Nexus offered: show its card and light up the setting it is about.
    function offered(ev) {
        cards.offer(ev);
        const a = ev.args || {};
        if (ev.action === "open_settings" && a.page) point(a.page, a.item || "");
        else if (ev.action === "change_setting" && settingPlaces[a.setting]) point(settingPlaces[a.setting][0], settingPlaces[a.setting][1]);
    }

    Connections {
        target: box.bridge
        function onDelta(id, text) { if (id === box.chatId) box.reply += text }
        function onAction(id, ev) { if (id === box.chatId) box.offered(ev) }
        function onDone(id, provider) { if (id === box.chatId) box.thinking = false }
        function onFailed(id, message) { if (id === box.chatId) { box.thinking = false; box.error = message } }
    }

    visible: shown
    height: Math.min(maxHeight, body.implicitHeight)
    property real maxHeight: 600
    radius: 22; clip: true
    color: "#0B1430"; border.color: Qt.rgba(0.53, 0.66, 1, 0.55)

    Flickable {
        anchors.fill: parent
        contentHeight: body.implicitHeight; clip: true
        boundsBehavior: Flickable.StopAtBounds
        Column {
            id: body
            width: box.width

            // What the person wants, in their words.
            Item {
                width: parent.width; height: 60
                Image { x: 20; anchors.verticalCenter: parent.verticalCenter; width: 18; height: 18
                        sourceSize: Qt.size(36, 36); source: "../icons/spark.svg" }
                TextInput {
                    id: askField
                    x: 50; width: parent.width - 100; anchors.verticalCenter: parent.verticalCenter
                    color: box.theme.ink; font.pixelSize: 16; clip: true
                    Text { visible: !askField.text; text: "What do you want? e.g. make text bigger"
                           color: box.theme.inkSecondary; font.pixelSize: 16 }
                    onAccepted: box.ask(text)
                    Keys.onEscapePressed: box.close()
                    Accessible.role: Accessible.EditableText
                    Accessible.name: "Ask or search settings"
                }
                Text {
                    anchors { right: parent.right; rightMargin: 20; verticalCenter: parent.verticalCenter }
                    text: "✕"; color: box.theme.inkSecondary; font.pixelSize: 15
                    TapHandler { onTapped: box.close() }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Close"
                }
                Rectangle { anchors.bottom: parent.bottom; width: parent.width; height: 1; color: "#1B2647" }
            }

            // What Nexus says and offers.
            Column {
                visible: box.question !== ""
                x: 20; width: parent.width - 40; spacing: 10; topPadding: 16; bottomPadding: 16
                Text { text: "NEXUS SUGGESTS"; color: box.theme.inkSecondary; font.pixelSize: 12; font.letterSpacing: 0.5 }
                Text { visible: box.thinking && box.reply === ""; text: "Thinking…"; color: "#FFA65C"; font.pixelSize: 13 }
                Text { visible: box.reply !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: box.reply.trim(); color: box.theme.ink; font.pixelSize: 14; lineHeight: 1.35 }
                Text { visible: box.error !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: box.error; color: box.theme.error; font.pixelSize: 13 }
                // Personal settings: "Do it" or "Not now"; system changes: "Review the change" (Guardian).
                ActionCards {
                    id: cards
                    width: parent.width
                    theme: box.theme; bridge: box.bridge; settings: box.host; desktop: box.host.desktop
                    apps: box.cardHost ? box.cardHost.apps : null
                    appsWin: box.cardHost ? box.cardHost.appsWin : null
                    imagesWin: box.cardHost ? box.cardHost.imagesWin : null
                    codeWin: box.cardHost ? box.cardHost.codeWin : null
                    fixItWin: box.cardHost ? box.cardHost.fixItWin : null
                    overview: box.cardHost ? box.cardHost.overview : null
                    moveInWin: box.cardHost ? box.cardHost.moveInWin : null
                    firstRun: box.cardHost ? box.cardHost.firstRun : null
                    onOpenRole: role => { if (box.cardHost) box.cardHost.openRole(role) }
                    onReview: card => {
                        if (!box.cardHost) return;
                        box.cardHost.reviewTaken = false;
                        box.cardHost.review(card);
                        cards.reviewTaken = box.cardHost.reviewTaken;
                    }
                }
                Text {
                    visible: !box.thinking && box.bridge && box.bridge.online
                    text: "Continue in Nexus"; color: box.theme.focus; font.pixelSize: 12
                    TapHandler { onTapped: { const q = box.question; box.close(); box.host.askNexus(q) } }
                    Accessible.role: Accessible.Button
                    Accessible.name: "Continue in Nexus"
                }
            }
            Rectangle { visible: box.question !== ""; width: parent.width; height: 1; color: "#1B2647" }

            // The settings that match the words, from Settings' own index.
            Column {
                visible: box.matches.length > 0
                x: 12; width: parent.width - 24; spacing: 2; topPadding: 12; bottomPadding: 12
                Text { leftPadding: 10; bottomPadding: 6; text: "MATCHING SETTINGS"; color: box.theme.inkSecondary; font.pixelSize: 12; font.letterSpacing: 0.5 }
                Repeater {
                    model: box.matches
                    delegate: Rectangle {
                        required property var modelData
                        required property int index
                        readonly property var pageInfo: box.host.pages.find(p => p.id === modelData.page)
                        readonly property bool lit: box.host.page === modelData.page && box.host.highlight === modelData.highlight
                        width: parent.width; height: 40; radius: 10
                        color: lit || mh.hovered ? "#121B33" : "transparent"
                        Rectangle { x: 10; anchors.verticalCenter: parent.verticalCenter; width: 10; height: 10; radius: 5; color: pageInfo.colour }
                        Text { x: 32; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 44; elide: Text.ElideRight
                               textFormat: Text.StyledText; font.pixelSize: 14; color: box.theme.ink
                               text: modelData.label + " <font color='#9AA9C9' size='2'>· " + pageInfo.label + "</font>" }
                        HoverHandler { id: mh; cursorShape: Qt.PointingHandCursor }
                        TapHandler { onTapped: box.point(modelData.page, modelData.highlight) }
                        Accessible.role: Accessible.Button
                        Accessible.name: modelData.label + ", " + pageInfo.label
                    }
                }
            }
            Text {
                width: parent.width; horizontalAlignment: Text.AlignHCenter; topPadding: 4; bottomPadding: 14
                color: "#7885A3"; font.pixelSize: 12; wrapMode: Text.Wrap
                text: "Nexus only suggests. Nothing changes until you choose Do it."
            }
        }
    }
}
