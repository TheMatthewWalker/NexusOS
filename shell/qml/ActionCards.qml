import QtQuick
import Quickshell
import Quickshell.Hyprland
import Quickshell.Services.Pipewire
import "settings"

// What Nexus offers to do, under its reply: one card per action, with what it
// will do in one plain sentence (Core writes it from the checked arguments,
// never the model). Nothing runs until the person chooses:
//  - personal actions: "Do it" or "Not now". "Do it" is the person's pick, so
//    the rule "apps open only when the user picks them" holds. Core then runs
//    the action, or hands back an instruction for the desktop, done here:
//    open an app or a Settings page, a desktop setting (journalled by
//    DesktopSettings.set), the volume (journalled here, with its old value),
//    Flathub, apt in a terminal the person can see, Images, Code, Fix it;
//  - system actions: "Review the change". The shell asks for the Guardian
//    dialog; without one, the plan is sent to Guardian, which decides.
// The text an action carries (a coding task, a picture's description) is
// shown whole, wrapped and scrolling, up to Core's limit (it says if cut).
// When a card's action is done (or fails), finished() lets the chat ask Nexus
// for one short follow-up. Desktop actions confirmed from a terminal
// (`nexus actions confirm`) are collected from Core and done here too.
Column {
    id: cards
    property var theme
    property var bridge
    property var apps            // AppDirectory
    property var settings
    property var desktop
    property var appsWin
    property var imagesWin
    property var codeWin
    property var fixItWin
    property var overview
    property var moveInWin
    property var firstRun
    property var cardList: []    // [{ id, action, safety, summary, detail, plan, state, message }]
    property var calls: ({})     // bridge request id -> [card id, "confirm" | "decline"]
    readonly property string terminal: Quickshell.env("NEXUS_TERMINAL") || "kitty"
    // An app or a window: the shell closes Nexus so it can be seen.
    signal opened()
    signal openRole(string role)
    // A system change to review; returns true if a Guardian dialog took it.
    signal review(var card)
    property bool reviewTaken: false
    // The action ran (ok) or failed; message is what the desktop or Core said.
    signal finished(string cardId, bool ok, string message)
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    property int collectId: -1

    spacing: 8
    visible: cardList.length > 0

    function clear() { cardList = []; calls = ({}) }
    function offer(ev) {
        // actionId: the bridge's own request id replaces "id" on every event.
        cardList = cardList.concat([{ id: ev.actionId, action: ev.action, safety: ev.safety, summary: ev.summary,
                                      detail: ev.detail || "", detailTruncated: !!ev.detailTruncated, detailLength: ev.detailLength || 0,
                                      plan: ev.plan || null, details: ev.details || null, state: "offered", message: "" }]);
    }
    function update(id, change) {
        cardList = cardList.map(c => c.id === id ? Object.assign({}, c, change) : c);
    }
    function call(card, verb) {
        if (!bridge || !bridge.online) { update(card.id, { state: "failed", message: "Nexus Core is offline." }); return }
        const rid = bridge.api("POST", "/actions/" + card.id + "/" + verb, {});
        if (rid < 0) return;
        const m = calls; m[rid] = [card.id, verb]; calls = m;
        update(card.id, { state: "working", message: verb === "confirm" ? "Working on it…" : "" });
    }
    function doIt(card) { call(card, "confirm") }
    function notNow(card) { call(card, "decline") }
    function reviewChange(card) {
        reviewTaken = false;
        review(card);
        if (reviewTaken) update(card.id, { state: "done", message: "Opened in Guardian. Nothing changes until you approve it there." });
        else call(card, "confirm");     // no dialog: send the plan to Guardian
    }

    Connections {
        target: cards.bridge
        function onResult(id, type, data) {
            if (id === cards.collectId) { cards.collectId = -1; cards.collected(data.items || []); return }
            const entry = cards.calls[id];
            if (!entry) return;
            const m = cards.calls; delete m[id]; cards.calls = m;
            const [cardId, verb] = entry;
            if (verb === "decline") { cards.update(cardId, { state: "declined", message: "Not done." }); return }
            let ok, message;
            if (data.status === "perform") {
                const said = cards.perform(data.perform);
                ok = said.ok; message = said.message;
            } else if (data.status === "sent") {
                ok = true; message = "Sent to Guardian. Nothing changes until you approve it.";
            } else {
                ok = data.status === "done"; message = data.message || "";
            }
            cards.update(cardId, { state: ok ? "done" : "failed", message: message });
            cards.finished(cardId, ok, message);
        }
        function onFailed(id, message) {
            const entry = cards.calls[id];
            if (!entry) return;
            const m = cards.calls; delete m[id]; cards.calls = m;
            cards.update(entry[0], { state: "failed", message: message });
        }
    }

    // Desktop actions someone confirmed in a terminal: Core hands them over
    // (DesktopHandoff) and they are done here, as if "Do it" was clicked.
    Timer {
        interval: 3000; running: true; repeat: true
        onTriggered: if (cards.bridge && cards.bridge.online && cards.collectId < 0)
                         cards.collectId = cards.bridge.api("POST", "/actions/desktop/collect", {})
    }
    function collected(items) {
        for (const item of items) {
            const said = perform(item.perform);
            if (cardList.some(c => c.id === item.id)) update(item.id, { state: said.ok ? "done" : "failed", message: said.message });
            else Quickshell.execDetached([hyprctl, "notify", "-1", "5000", "rgb(86a8ff)", "Nexus: " + item.summary + " — " + said.message]);
        }
    }

    // Instructions from Core for things only the desktop can do.
    function perform(p) {
        switch (p.op) {
        case "app.open": {
            // A role (files, browser, terminal, settings, studio) opens as from the orbit.
            if (p.role) {
                if (!["settings", "studio"].includes(p.app) && !apps.installed(p.app)) return { ok: false, message: "That app is not installed." };
                cards.openRole(p.app);
                return { ok: true, message: "Opened." };
            }
            const entry = DesktopEntries.byId(String(p.app).replace(/\.desktop$/, ""));
            if (!entry) return { ok: false, message: "That app is not installed." };
            entry.execute(); cards.opened();
            return { ok: true, message: "Opened." };
        }
        case "settings.open": settings.open(p.page, p.item || ""); cards.opened(); return { ok: true, message: "Opened in Settings." };
        case "desktop.set": desktop.set(p.key, p.value); return { ok: true, message: "Done. Undo it in Settings, Timeline." };
        case "volume.set": {
            const sink = Pipewire.defaultAudioSink;
            if (!sink || !sink.audio) return { ok: false, message: "No sound output was found." };
            const before = Math.round(sink.audio.volume * 100);
            sink.audio.volume = Math.max(0, Math.min(1, p.value / 100));
            if (p.value > 0) sink.audio.muted = false;
            if (bridge && bridge.online)
                bridge.api("POST", "/journal", { kind: "setting", summary: "Volume " + p.value + "%", before: { value: before },
                                                 after: { value: p.value }, reversible: true, ref: "sound:volume" });
            return { ok: true, message: "Done. Undo it in Settings, Timeline." };
        }
        // Installs, removals and their Undo run in Apps, which journals them (Apps.perform); apt in a terminal.
        case "flathub.install": case "flathub.remove": case "apt.install": case "apt.remove": {
            const said = appsWin.perform(p);
            if (said.ok) cards.opened();
            return said;
        }
        case "images.make": imagesWin.make(p.prompt); cards.opened(); return { ok: true, message: "Making it in Images…" };
        case "code.start": codeWin.start(p.folder, p.task); cards.opened(); return { ok: true, message: "Opened in Code with the task filled in; press Enter there to send it." };
        case "fixit.open": fixItWin.open(p.problem); cards.opened(); return { ok: true, message: "Checking in Fix it…" };
        case "workspace.save": {
            const ws = Hyprland.focusedWorkspace ? Hyprland.focusedWorkspace.id : 1;
            return overview.remember(ws, p.name) ? { ok: true, message: "Remembered. Say “open " + p.name + "” to bring it back." }
                                                 : { ok: false, message: overview.message || "Could not remember it." };
        }
        case "workspace.restore":
            return overview.restore(p.name) ? { ok: true, message: "Bringing it back…" } : { ok: false, message: overview.message || "No workspace by that name." };
        case "app.try": appsWin.open("try"); appsWin.tryApp(p.package); cards.opened(); return { ok: true, message: "Starting it in a sandbox…" };
        case "movein.open": moveInWin.open(); cards.opened(); return { ok: true, message: "Opened." };
        case "welcome.open": firstRun.open(); cards.opened(); return { ok: true, message: "Opened." };
        default: return { ok: false, message: "This desktop cannot do that yet." };
        }
    }

    Repeater {
        model: cards.cardList
        delegate: Rectangle {
            required property var modelData
            width: cards.width
            height: body.implicitHeight + 24
            radius: 14
            color: cards.theme.chip
            border.color: modelData.safety === "system" ? cards.theme.focus : cards.theme.glassBorder
            border.width: 1
            Column {
                id: body
                x: 14; y: 12; width: parent.width - 28
                spacing: 8
                Text {
                    width: parent.width
                    text: modelData.safety === "system" ? "System change · needs your approval in Guardian" : "Nexus can do this for you"
                    font.pixelSize: 12; color: cards.theme.inkSecondary
                }
                Text {
                    width: parent.width; wrapMode: Text.Wrap
                    textFormat: Text.PlainText
                    text: modelData.summary
                    font.pixelSize: 14; font.weight: 550; color: cards.theme.ink
                }
                // The whole text the action carries, scrolling past a few lines.
                Flickable {
                    id: detailView
                    visible: modelData.detail !== ""
                    width: parent.width
                    height: Math.min(detailText.implicitHeight, 160)
                    contentWidth: width; contentHeight: detailText.implicitHeight
                    clip: true
                    boundsBehavior: Flickable.StopAtBounds
                    Text {
                        id: detailText
                        width: detailView.width - 8; wrapMode: Text.Wrap
                        textFormat: Text.PlainText      // model words are never rendered as rich text
                        text: modelData.detail
                        font.pixelSize: 13; lineHeight: 1.4; color: cards.theme.ink
                    }
                    Rectangle {
                        visible: detailView.contentHeight > detailView.height
                        x: detailView.width - 3; width: 3; radius: 1.5
                        y: detailView.contentY + detailView.visibleArea.yPosition * detailView.height
                        height: Math.max(16, detailView.visibleArea.heightRatio * detailView.height)
                        color: cards.theme.glassBorder
                    }
                }
                Text {
                    visible: modelData.detailTruncated
                    width: parent.width; wrapMode: Text.Wrap
                    text: "Shortened here: showing the first " + modelData.detail.length + " of " + modelData.detailLength + " characters."
                    font.pixelSize: 12; font.italic: true; color: cards.theme.inkSecondary
                }
                // Facts Core looked up for this card: a Flathub app's publisher, licence and permissions; what apt is.
                Text {
                    width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                    visible: !!(modelData.details && modelData.details.warning)
                    text: modelData.details && modelData.details.warning ? modelData.details.warning : ""
                    font.pixelSize: 13; font.weight: 500; color: "#FFC48F"
                }
                Repeater {
                    model: modelData.details && modelData.details.lines ? modelData.details.lines.slice(0, 12) : []
                    delegate: Text {
                        required property var modelData
                        width: body.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                        text: "· " + modelData; font.pixelSize: 12; color: cards.theme.inkSecondary
                    }
                }
                Row {
                    spacing: 8
                    visible: modelData.state === "offered"
                    Button {
                        theme: cards.theme; primary: true
                        label: modelData.safety === "system" ? "Review the change" : "Do it"
                        onClicked: modelData.safety === "system" ? cards.reviewChange(modelData) : cards.doIt(modelData)
                    }
                    Button { theme: cards.theme; label: "Not now"; onClicked: cards.notNow(modelData) }
                }
                Text {
                    width: parent.width; wrapMode: Text.Wrap
                    visible: modelData.state !== "offered" && modelData.message !== ""
                    textFormat: Text.PlainText
                    text: modelData.message
                    font.pixelSize: 13; color: modelData.state === "failed" ? cards.theme.error : cards.theme.inkSecondary
                }
            }
        }
    }
}
