import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Services.Polkit
import "settings"
import "lib/guardian.mjs" as GD

// Guardian: the system change dialog (design: "System change proposal
// (Guardian)"). It shows what a proposed system change is in plain words,
// every item it changes with before and after, what Guardian checked, the
// risk, and that it is built and tried at the next restart and can go back;
// then Approve or Decline. It also hosts the desktop's polkit agent, so the
// password for an approval is typed here.
//
// Safety rules:
//  - nothing is approved without the person: "Approve and stage" starts
//    Guardian's checks, and approval itself is polkit's password prompt
//    (auth_admin, every time) for nexus-guardian-desktop-approve. This file
//    never types, stores or remembers a password, and never approves on a
//    timer or on Nexus's say-so.
//  - what is approved is what Guardian holds, not what Core's proposals file
//    says (anything running as this user can rewrite that file). Once the
//    checks pass, the dialog shows the broker's own record of the candidate
//    (GET /guardian/proposals/{id}/describe: each setting with today's and the
//    new value, packages, risk, candidate ID, build hash); Core's title is only
//    a "Nexus says" note, and if the two disagree on what changes the broker's
//    is shown with a warning. The helper is run as `<candidateId> <buildHash>`
//    from that record; polkit's prompt names that command line, and after the
//    password the helper checks both against the broker again
//    (desktop_approve.py).
//  - the polkit agent types a password into this dialog only for Guardian's
//    approve action naming this dialog's candidate and build (lib/guardian.mjs
//    ownsFlow); every other request gets the plain window showing its action
//    and message as they are.
//  - the broker decides: after the password, the dialog asks Core for the
//    existing review (POST /guardian/review with the builder's attestation)
//    and stages only an eligible decision (POST /guardian/stage).
//  - it talks to Core only through CoreBridge (bridge.api).
//
// For Nexus (the chat tools) and anything else that proposes a change:
//   POST /guardian/propose   {"request": "<what the person asked, 1-1000 chars>"}
//       The model turns it into a typed plan (allowlisted options and/or
//       nixpkgs packages). Answer: {proposed, plan, proposal: {id, state:
//       "waiting", ...}, submitted: false} or {proposed: false, refusal}.
//       The proposal appears here; this dialog opens by itself within a few seconds.
//   POST /guardian/proposals {"description": "...", "changes": [{"option", "enabled"}],
//                             "packages": ["attr"], "source": "nexus"|"fixit"|"try"|"user",
//                             "request": "<optional, at most 300 chars>"}
//       The same with a typed plan (validated by Core; the same open plan is not repeated).
//   GET  /guardian/proposals -> {proposals: [...], attention, busy, restartNeeded}
//   Each proposal: {id, source, request, state, message, title, plan, items: [{kind:
//   "setting"|"package", name, label, before, after}], risk: {level, notes}, checks:
//   {evaluate, build, vm-test}, candidateId, attestation, decision}. States: waiting,
//   checking, ready, approved, staged, applied, rolled-back, declined, failed.
//   GET  /guardian/proposals/{id}/describe (a "ready" proposal) -> the broker's record:
//   {candidateId, buildHash, settings: [{option, summary, before, after}], packages:
//   [{package, before}], risk, nixModule, staged}. This is what the dialog shows and approves.
//   Proposing never submits, approves or changes anything; only the person can, here.
//
// Shell entry points: open() shows waiting changes; open(planId, plan) records a
// plan from an action card (Nexus's "Review the change") and shows it;
// openPlan(plan, source) does the same for the rest of the shell (First run);
// open(actionId) fetches that action's plan first. Also
// `nexus-shell-ipc call guardian open` and `... guardian state`.
Scope {
    id: win
    property var theme
    property var bridge
    property alias visible: dialog.visible

    property var proposals: []
    property int index: 0
    readonly property var current: proposals.length > 0 ? proposals[Math.min(index, proposals.length - 1)] : null
    property bool attention: false
    property bool busy: false
    property bool restartNeeded: false
    property string note: ""              // what the dialog itself has to say (errors, progress)
    property bool showFull: false
    property string approveWhenReady: ""  // the person chose Approve; ask for the password once checks pass
    property bool approving: false        // polkit is asking for the password for our approval
    property var seen: ({})               // proposals already shown by themselves
    property var calls: ({})              // bridge request id -> [kind, proposal id]
    property var attested: null           // the broker's record of a checked proposal (describe)
    property string attestedFor: ""       // ... and which proposal it was fetched for
    // The broker's record of the proposal on screen, once it has passed the checks.
    readonly property var held: !!current && current.state === "ready" && attestedFor === current.id && GD.isRecord(attested)
                                  && attested.candidateId === current.candidateId ? attested : null
    readonly property bool disagrees: !!held && !GD.agrees(current.items, held)
    readonly property var shownItems: held ? GD.items(held) : current ? current.items : []
    readonly property int shownPackages: shownItems.filter(i => i.kind === "package").length
    readonly property int shownSettings: shownItems.length - shownPackages
    readonly property var shownRisk: held ? held.risk : current ? current.risk : ({ level: "", notes: [] })
    readonly property string pkexec: Quickshell.env("NEXUS_PKEXEC") || "/run/wrappers/bin/pkexec"
    readonly property string approveHelper: Quickshell.env("NEXUS_GUARDIAN_APPROVE") || "/run/current-system/sw/bin/nexus-guardian-desktop-approve"
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    readonly property bool finished: !!current && ["applied", "rolled-back", "declined", "failed"].includes(current.state)

    function call(kind, method, path, body, id) {
        if (!bridge || !bridge.online) { note = "Nexus Core is not running, so system changes cannot be shown."; return -1 }
        const rid = bridge.api(method, path, body);
        if (rid >= 0) { const m = calls; m[rid] = [kind, id || ""]; calls = m }
        return rid;
    }
    function refresh() { call("list", "GET", "/guardian/proposals") }
    function show(id) {
        const i = proposals.findIndex(p => p.id === id);
        if (i >= 0) index = i;
    }
    function open(id, plan) {
        if (dialog.visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(System change)$"]);
        dialog.visible = true; note = ""; showFull = false;
        if (plan && plan.description) record(plan);
        else if (typeof id === "string" && /^[0-9a-f]{24}$/.test(id)) call("action", "GET", "/actions/" + id);
        else if (typeof id === "string" && /^[0-9a-f]{16}$/.test(id)) { show(id); refresh() }
        else refresh();
    }
    // A typed plan (from an action card, or First run's "for every account" choices): Core
    // validates it again and keeps it as a proposal waiting for the person.
    function openPlan(plan, source) {
        if (dialog.visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(System change)$"]);
        dialog.visible = true; note = ""; showFull = false;
        record(plan, source);
    }
    function record(plan, source) {
        call("created", "POST", "/guardian/proposals", { description: plan.description, changes: plan.changes || [],
                                                          packages: plan.packages || [], source: source || "nexus" });
    }
    function approve() {
        const p = current;
        if (!p) return;
        note = "";
        if (p.state === "ready") {
            // The person has the broker's record in front of them (warned if Nexus said otherwise).
            if (held) askPassword(p, held);
            else { approveWhenReady = p.id; describe(p) }
            return;
        }
        if (p.state !== "waiting" && p.state !== "failed") return;
        approveWhenReady = p.id;
        call("verb", "POST", "/guardian/proposals/" + p.id + "/check", {}, p.id);
    }
    // The broker's own record of a checked proposal: what the person sees and approves.
    function describe(p) {
        if (p && p.state === "ready" && !pending("describe", p.id)) call("describe", "GET", "/guardian/proposals/" + p.id + "/describe", undefined, p.id);
    }
    function pending(kind, id) { return Object.values(calls).some(e => e[0] === kind && e[1] === id) }
    function decline() { if (current) call("verb", "POST", "/guardian/proposals/" + current.id + "/decline", {}, current.id) }
    function dismiss() {
        if (current && finished) call("verb", "POST", "/guardian/proposals/" + current.id + "/dismiss", {}, current.id);
        else dialog.visible = false;
    }
    // Polkit runs the approval helper as root after the person's password; nothing else approves.
    // It is named the candidate and build from the broker's record, which is what is on screen.
    function askPassword(p, r) {
        if (approver.running || !GD.isRecord(r) || r.candidateId !== p.candidateId) return;
        approveWhenReady = "";
        if (r.staged) { note = "This change has already been set up for the next restart."; return }
        approving = true;
        approver.proposal = p;
        approver.record = r;
        approver.command = [pkexec, approveHelper].concat(GD.helperArgs(r));
        approver.running = true;
    }
    function accept(list) {
        proposals = list.proposals || [];
        attention = !!list.attention; busy = !!list.busy; restartNeeded = !!list.restartNeeded;
        if (index >= proposals.length) index = 0;
        // A change waiting for the person opens the dialog by itself, once.
        const fresh = proposals.find(p => (p.state === "waiting" || p.state === "ready") && !seen[p.id]);
        if (fresh) {
            const m = seen; m[fresh.id] = true; seen = m;
            if (!dialog.visible || current && current.state !== "ready") show(fresh.id);
            dialog.visible = true;
        }
        // A checked change: fetch the broker's record before anything asks for a password.
        if (current && current.state === "ready" && attestedFor !== current.id) describe(current);
        if (current && current.id === approveWhenReady && current.state === "ready" && held && !disagrees) askPassword(current, held);
        if (current && current.id === approveWhenReady && current.state === "failed") approveWhenReady = "";
    }
    // The broker's record arrived. Continue to the password only if Nexus described the same
    // changes; otherwise the person sees the warning and decides with Approve.
    function described(id, data) {
        attested = data; attestedFor = id;
        if (!current || current.id !== id) return;
        if (!held) { if (approveWhenReady === id) approveWhenReady = ""; note = "Guardian's record of this change could not be read, so it cannot be approved now."; return }
        if (approveWhenReady === id) {
            if (disagrees) approveWhenReady = "";
            else askPassword(current, held);
        }
    }
    function replace(p) {
        const i = proposals.findIndex(x => x.id === p.id);
        proposals = i >= 0 ? proposals.map(x => x.id === p.id ? p : x) : [p].concat(proposals);
        show(p.id);
    }
    function onOff(value) { return value === true ? "On" : value === false ? "Off" : "Not known yet" }
    function itemChange(item) {
        if (item.kind === "package") return (item.before === true ? "Installed" : item.before === false ? "Not installed" : "Not known yet") + "  →  Installed";
        return onOff(item.before) + "  →  " + onOff(item.after);
    }
    function heading(p) {
        if (!p) return "";
        return p.source === "nexus" ? "NEXUS PROPOSES A SYSTEM CHANGE" : p.source === "try" ? "KEEP AN APP YOU TRIED"
             : p.source === "fixit" ? "FIX IT PROPOSES A SYSTEM CHANGE" : "A SYSTEM CHANGE";
    }
    function checkMark(state) { return state === "pass" ? "✓" : state === "fail" ? "✕" : state === "running" ? "…" : "○" }
    function checkColour(state) { return state === "pass" ? "#7FE0B0" : state === "fail" ? theme.error : state === "running" ? theme.focus : theme.inkSecondary }

    Connections {
        target: win.bridge
        function onResult(id, type, data) {
            const entry = win.calls[id];
            if (!entry) return;
            const m = win.calls; delete m[id]; win.calls = m;
            const kind = entry[0];
            if (kind === "list") win.accept(data);
            else if (kind === "describe") win.described(entry[1], data);
            else if (kind === "action") {
                if (data.plan) win.record(data.plan); else win.note = "That offer has expired. Ask Nexus again.";
            } else if (kind === "created" || kind === "verb") { win.replace(data); win.refresh() }
            else if (kind === "review") {
                if (data.eligible === true && data.decision) {
                    win.note = "Approved. Setting it up for the next restart…";
                    win.call("stage", "POST", "/guardian/stage", { decision: data.decision }, entry[1]);
                } else { win.note = "Guardian did not accept this change after checking it. Nothing changed."; win.refresh() }
            } else if (kind === "stage") { win.note = ""; win.refresh() }
        }
        function onFailed(id, message) {
            const entry = win.calls[id];
            if (!entry) return;
            const m = win.calls; delete m[id]; win.calls = m;
            win.note = message;
            if ((entry[0] === "verb" || entry[0] === "describe") && entry[1] === win.approveWhenReady) win.approveWhenReady = "";
            if (entry[0] !== "list") win.refresh();
        }
    }
    // Quick while something is under way, slow otherwise.
    Timer {
        interval: win.busy || win.approving || win.approveWhenReady !== "" ? 2500 : win.restartNeeded ? 60000 : 20000
        running: !!win.bridge && win.bridge.online; repeat: true; triggeredOnStart: true
        onTriggered: win.refresh()
    }

    Process {
        id: approver
        property var proposal: null
        property var record: null     // the broker's record the helper was named for
        stdout: StdioCollector { id: approverOut }
        onExited: (code) => {
            win.approving = false;
            const p = approver.proposal;
            if (code === 0 && p) {
                win.note = "Approved with your password. Guardian is reviewing it…";
                win.call("review", "POST", "/guardian/review", { candidateId: p.candidateId, attestation: p.attestation }, p.id);
            } else {
                // 126: the password prompt was dismissed; 127: not authorised.
                win.note = code === 126 || code === 127 ? "Not approved. Nothing changed."
                    : "Not approved: " + ((() => { try { return JSON.parse(approverOut.text).error } catch (e) { return "the approval did not go through" } })()) + ". Nothing changed.";
            }
        }
    }

    // The desktop's polkit agent: our approval's password is typed in the dialog;
    // any other request gets its own small window. Ours is Guardian's approve
    // action whose message (polkit fills in the helper's command line) names
    // exactly the candidate and build this open dialog asked about.
    PolkitAgent { id: polkit }
    readonly property var flow: polkit.flow
    readonly property bool ownFlow: !!flow && approving && !!approver.record
                                    && GD.ownsFlow(flow.actionId, flow.message, approver.record)
    onFlowChanged: {
        if (!flow) return;
        // Ask for this person's own password when they are one of the allowed identities.
        const me = Quickshell.env("USER");
        const mine = (flow.identities || []).find(i => !i.isGroup && (i.string === me || i.displayName === me));
        if (mine && flow.selectedIdentity !== mine) flow.selectedIdentity = mine;
    }

    FloatingWindow {
        id: dialog
        title: "System change"
        visible: false
        implicitWidth: 720; implicitHeight: 760
        minimumSize: Qt.size(600, 520)
        color: "#05071A"

        Rectangle {
            anchors.fill: parent
            gradient: Gradient {
                GradientStop { position: 0; color: "#0B0F24" }
                GradientStop { position: 1; color: "#020309" }
            }
        }
        Rectangle {
            id: card
            anchors { fill: parent; margins: 18 }
            radius: 22; color: Qt.rgba(0.04, 0.07, 0.15, 0.97)
            border.color: Qt.rgba(0.53, 0.66, 1.0, 0.6)
            clip: true

            Flickable {
                anchors { left: parent.left; right: parent.right; top: parent.top; bottom: footer.top; margins: 26; bottomMargin: 12 }
                contentHeight: body.implicitHeight; clip: true
                boundsBehavior: Flickable.StopAtBounds
                Column {
                    id: body
                    width: parent.width; spacing: 16

                    Text {
                        visible: !win.current; width: parent.width; wrapMode: Text.Wrap
                        text: win.note || "No system changes are waiting. When Nexus proposes one, or you keep an app you tried, it appears here for your approval."
                        color: win.theme.inkSecondary; font.pixelSize: 14
                    }

                    // Header: shield, who proposes it, the plain summary.
                    Row {
                        visible: !!win.current; spacing: 16; width: parent.width
                        Rectangle {
                            width: 44; height: 44; radius: 14; color: "#121B33"; border.color: "#2A3A66"
                            Image { anchors.centerIn: parent; width: 22; height: 22; sourceSize: Qt.size(44, 44); source: "icons/guardian.svg" }
                        }
                        Column {
                            width: parent.width - 60; spacing: 4; anchors.verticalCenter: parent.verticalCenter
                            Text { text: win.heading(win.current); color: win.theme.inkSecondary; font.pixelSize: 12; font.letterSpacing: 0.5 }
                            Text {
                                width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                                // Once checked, the broker's record names the change; before that, Nexus's summary.
                                text: win.held ? GD.title(win.held) : win.current ? win.current.title : ""
                                color: win.theme.ink; font.pixelSize: 21; font.weight: 600
                                Accessible.role: Accessible.Heading
                            }
                            Text {
                                visible: !!win.held; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                                text: win.current ? "Nexus says: “" + win.current.title + "”" : ""
                                color: win.theme.inkSecondary; font.pixelSize: 13; font.italic: true
                            }
                        }
                    }
                    // Guardian's record and Nexus's summary name different changes: Guardian's is shown and approved.
                    Text {
                        visible: win.disagrees; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                        text: "!  Guardian's record of this change is not what Nexus described. What is shown here is Guardian's record, and it is what you would approve. If you did not expect it, decline."
                        color: "#FFD166"; font.pixelSize: 13; font.weight: 500
                        Accessible.role: Accessible.AlertMessage
                    }
                    Text {
                        visible: !!win.current; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                        color: win.theme.inkSecondary; font.pixelSize: 13; lineHeight: 1.3
                        text: !win.current ? "" : (win.current.request ? "You asked: “" + win.current.request + "”. " : "")
                              + (["waiting", "checking", "ready", "failed"].includes(win.current.state)
                                 ? "Nothing changes until you approve. The change is built and checked on this computer first, then used from the next restart."
                                 : "")
                    }

                    // Packages, settings, risk.
                    Row {
                        visible: !!win.current; width: parent.width; spacing: 10
                        Repeater {
                            model: !win.current ? [] : [
                                { k: "Packages", v: win.shownPackages ? "+" + win.shownPackages + " added" : "None" },
                                { k: "Settings", v: win.shownSettings ? win.shownSettings + " changed" : "None" },
                                { k: "Risk", v: win.shownRisk.level === "elevated" ? "Higher · can be undone" : "Low · can be undone",
                                  c: win.shownRisk.level === "elevated" ? "#FFD166" : "#7FE0B0" }
                            ]
                            delegate: Rectangle {
                                required property var modelData
                                width: (body.width - 20) / 3; height: 62; radius: 12; color: "#0E1630"; border.color: "#1F2C55"
                                Column {
                                    x: 14; anchors.verticalCenter: parent.verticalCenter; spacing: 4
                                    Text { text: modelData.k; color: win.theme.inkSecondary; font.pixelSize: 12 }
                                    Text { text: modelData.v; color: modelData.c || win.theme.ink; font.pixelSize: 15; font.weight: 500 }
                                }
                            }
                        }
                    }
                    Repeater {
                        model: win.shownRisk.notes || []
                        delegate: Text {
                            required property var modelData
                            width: body.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                            text: "!  " + modelData; color: "#FFD166"; font.pixelSize: 13
                        }
                    }

                    // Every item changed, before → after.
                    Rectangle {
                        visible: !!win.current; width: parent.width; radius: 12; color: "#070B1C"; border.color: "#1F2C55"
                        height: items.implicitHeight + 20
                        Column {
                            id: items
                            x: 16; y: 10; width: parent.width - 32; spacing: 8
                            Text {
                                width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                                text: win.held ? "What changes (Guardian's record: change " + win.held.candidateId + ", build " + win.held.buildHash.slice(0, 16) + ")"
                                                 : "What changes"
                                color: win.theme.inkSecondary; font.pixelSize: 12
                            }
                            Repeater {
                                model: win.shownItems
                                delegate: Row {
                                    required property var modelData
                                    width: items.width; spacing: 10
                                    Text { text: modelData.kind === "package" ? "+" : "~"; color: "#BDF0D6"; font.family: "JetBrains Mono"; font.pixelSize: 13; width: 14 }
                                    Column {
                                        width: parent.width - 24; spacing: 2
                                        Text {
                                            width: parent.width; elide: Text.ElideRight; textFormat: Text.PlainText
                                            text: modelData.kind === "package" ? modelData.name : modelData.label
                                            color: win.theme.ink; font.pixelSize: 14; font.weight: 500
                                        }
                                        Text {
                                            width: parent.width; elide: Text.ElideRight; textFormat: Text.PlainText
                                            text: (modelData.kind === "package" ? "Software for every account" : modelData.name) + "   " + win.itemChange(modelData)
                                            color: win.theme.inkSecondary; font.family: "JetBrains Mono"; font.pixelSize: 12
                                        }
                                    }
                                }
                            }
                            Text {
                                visible: win.showFull; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                                text: win.held ? win.held.nixModule || "" : win.current ? win.current.plan.nixPreview : ""; color: "#C9D4FF"
                                font.family: "JetBrains Mono"; font.pixelSize: 12; topPadding: 6
                            }
                        }
                    }

                    // What Guardian checks, and how far it has got.
                    Column {
                        visible: !!win.current; width: parent.width; spacing: 8
                        Accessible.name: "Checks"
                        Repeater {
                            model: !win.current ? [] : [
                                { m: "✓", c: "#7FE0B0", t: "Only allowed settings and packages are touched (checked against Guardian's rules, not by the AI)" },
                                { m: win.checkMark(win.current.checks.build), c: win.checkColour(win.current.checks.build),
                                  t: win.current.checks.evaluate === "fail" ? "It could not be put together with this computer's settings" : "Built on this computer" },
                                { m: win.checkMark(win.current.checks["vm-test"]), c: win.checkColour(win.current.checks["vm-test"]),
                                  t: "Starts up properly in a test machine" },
                                { m: win.current.attestation || ["approved", "staged", "applied"].includes(win.current.state) ? "✓" : "○",
                                  c: win.current.attestation || ["approved", "staged", "applied"].includes(win.current.state) ? "#7FE0B0" : win.theme.inkSecondary,
                                  t: "Signed by this computer, so Guardian knows exactly what was checked" },
                                { m: "↺", c: win.theme.focus, t: "Tried once at the next restart: if it does not start properly, the computer goes back to today's version by itself" }
                            ]
                            delegate: Row {
                                required property var modelData
                                width: body.width; spacing: 10
                                Text { text: modelData.m; color: modelData.c; font.pixelSize: 13; width: 14 }
                                Text { width: parent.width - 24; wrapMode: Text.Wrap; text: modelData.t; color: win.theme.ink; font.pixelSize: 13 }
                            }
                        }
                    }

                    // Where it is now.
                    Text {
                        visible: !!win.current && (win.current.message || win.note) !== "" && !win.ownFlow
                        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                        text: win.note || (win.current ? win.current.message || "" : "")
                        color: win.current && ["failed", "rolled-back"].includes(win.current.state) ? win.theme.error : win.theme.inkSecondary
                        font.pixelSize: 13
                        Accessible.role: Accessible.StaticText
                    }

                    // The password, for this approval only (polkit).
                    Rectangle {
                        visible: win.ownFlow; width: parent.width; radius: 14; color: win.theme.glass
                        border.color: password.activeFocus ? win.theme.focus : win.theme.glassBorder
                        height: pw.implicitHeight + 28
                        Column {
                            id: pw
                            x: 16; y: 14; width: parent.width - 32; spacing: 8
                            Text { width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; color: win.theme.ink; font.pixelSize: 14; font.weight: 500
                                   text: (approver.record ? GD.title(approver.record) + ". " : "") + "To approve, enter your password." }
                            Text { visible: !!win.flow && win.flow.supplementaryMessage !== ""; width: parent.width; wrapMode: Text.Wrap
                                   text: win.flow ? win.flow.supplementaryMessage : ""; font.pixelSize: 12
                                   color: win.flow && win.flow.supplementaryIsError ? win.theme.error : win.theme.inkSecondary }
                            Row {
                                width: parent.width; spacing: 8
                                Rectangle {
                                    width: parent.width - approveButton.width - 8; height: 38; radius: 10; color: win.theme.chip
                                    TextInput {
                                        id: password
                                        anchors { fill: parent; leftMargin: 12; rightMargin: 12 }
                                        verticalAlignment: TextInput.AlignVCenter
                                        echoMode: win.flow && win.flow.responseVisible ? TextInput.Normal : TextInput.Password
                                        color: win.theme.ink; font.pixelSize: 14; clip: true
                                        enabled: !!win.flow && win.flow.isResponseRequired
                                        onVisibleChanged: if (visible) forceActiveFocus()
                                        onAccepted: approveButton.clicked()
                                        Accessible.role: Accessible.EditableText
                                        Accessible.name: "Your password"
                                        Accessible.passwordEdit: true
                                    }
                                }
                                Button {
                                    id: approveButton
                                    theme: win.theme; primary: true; label: "Approve"
                                    enabled: !!win.flow && win.flow.isResponseRequired
                                    onClicked: { if (win.flow) win.flow.submit(password.text); password.text = "" }
                                }
                            }
                        }
                    }
                }
            }

            // Footer: full change, then Decline and Approve (or what fits the state).
            Rectangle {
                id: footer
                anchors { left: parent.left; right: parent.right; bottom: parent.bottom; margins: 1 }
                height: 82; color: "transparent"
                Rectangle { anchors { left: parent.left; right: parent.right; top: parent.top } height: 1; color: "#1B2647" }
                Row {
                    x: 26; anchors.verticalCenter: parent.verticalCenter; spacing: 14
                    Text {
                        visible: !!win.current; anchors.verticalCenter: parent.verticalCenter
                        text: win.showFull ? "Hide the full change" : "Show the full change"; color: "#86A8FF"; font.pixelSize: 13
                        HoverHandler { cursorShape: Qt.PointingHandCursor }
                        TapHandler { onTapped: win.showFull = !win.showFull }
                        Accessible.role: Accessible.Link
                        Accessible.name: text
                        Accessible.onPressAction: win.showFull = !win.showFull
                    }
                    Text {
                        visible: win.proposals.length > 1; anchors.verticalCenter: parent.verticalCenter
                        text: "‹ " + (win.index + 1) + " of " + win.proposals.length + " ›"; color: win.theme.inkSecondary; font.pixelSize: 13
                        TapHandler { onTapped: (ev) => win.index = (win.index + 1) % win.proposals.length }
                        Accessible.role: Accessible.Button
                        Accessible.name: "Next system change"
                    }
                }
                Row {
                    anchors { right: parent.right; rightMargin: 26; verticalCenter: parent.verticalCenter }
                    spacing: 10
                    readonly property string state: win.current ? win.current.state : ""
                    Button {
                        theme: win.theme
                        visible: ["waiting", "ready", "failed"].includes(parent.state) && !win.approving
                        label: "Decline"; onClicked: win.decline()
                    }
                    Button {
                        theme: win.theme
                        visible: !win.current || win.finished || ["checking", "approved", "staged"].includes(parent.state)
                        label: parent.state === "staged" ? "Later" : "Close"
                        onClicked: parent.state === "staged" || !win.finished ? dialog.visible = false : win.dismiss()
                    }
                    Button {
                        theme: win.theme; primary: true
                        visible: ["waiting", "ready", "checking"].includes(parent.state) || parent.state === "failed" && win.current.candidateId === null
                        enabled: parent.state !== "checking" && !win.approving && win.approveWhenReady === ""
                        label: parent.state === "checking" || win.approveWhenReady !== "" ? "Checking…" : win.approving ? "Waiting for your password…" : "Approve and stage"
                        onClicked: win.approve()
                    }
                    Button {
                        theme: win.theme; primary: true; visible: parent.state === "staged"
                        label: "Restart now"; onClicked: Quickshell.execDetached(["systemctl", "reboot"])
                    }
                }
            }
        }
    }

    // Any other polkit request (not this dialog's approval of what it shows): a small
    // password window with the request's action and message exactly as polkit sent them.
    FloatingWindow {
        id: other
        title: "Password needed"
        visible: !!win.flow && !win.ownFlow
        implicitWidth: 520; implicitHeight: 300
        color: "#05071A"
        Column {
            anchors { fill: parent; margins: 24 }
            spacing: 10
            Text { width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; color: win.theme.ink; font.pixelSize: 15; font.weight: 600
                   text: win.flow ? win.flow.message : "" }
            Text { width: parent.width; wrapMode: Text.WrapAnywhere; textFormat: Text.PlainText; color: win.theme.inkSecondary
                   font.family: "JetBrains Mono"; font.pixelSize: 12
                   text: win.flow ? "Requested action: " + win.flow.actionId : "" }
            // A Guardian approval that is not the one in the System change dialog.
            Text { visible: !!win.flow && win.flow.actionId === GD.APPROVE_ACTION; width: parent.width; wrapMode: Text.Wrap
                   textFormat: Text.PlainText; color: "#FFD166"; font.pixelSize: 13; font.weight: 500
                   text: "!  This asks to approve a system change that is not the one shown in the System change window. If you did not start it, cancel." }
            Text { visible: !!win.flow && win.flow.supplementaryMessage !== ""; width: parent.width; wrapMode: Text.Wrap; font.pixelSize: 12
                   text: win.flow ? win.flow.supplementaryMessage : ""
                   color: win.flow && win.flow.supplementaryIsError ? win.theme.error : win.theme.inkSecondary }
            Rectangle {
                width: parent.width; height: 38; radius: 10; color: win.theme.chip; border.color: otherPassword.activeFocus ? win.theme.focus : win.theme.glassBorder
                TextInput {
                    id: otherPassword
                    anchors { fill: parent; leftMargin: 12; rightMargin: 12 }
                    verticalAlignment: TextInput.AlignVCenter; clip: true
                    echoMode: win.flow && win.flow.responseVisible ? TextInput.Normal : TextInput.Password
                    color: win.theme.ink; font.pixelSize: 14
                    onVisibleChanged: if (visible) forceActiveFocus()
                    onAccepted: { if (win.flow) win.flow.submit(text); text = "" }
                    Accessible.role: Accessible.EditableText
                    Accessible.name: "Your password"
                    Accessible.passwordEdit: true
                }
            }
            Row {
                spacing: 10; anchors.right: parent.right
                Button { theme: win.theme; label: "Cancel"; onClicked: if (win.flow) win.flow.cancelAuthenticationRequest() }
                Button { theme: win.theme; primary: true; label: "OK"; onClicked: otherPassword.accepted() }
            }
        }
    }
    // Our own approval's prompt can be cancelled from the dialog's Decline/Close too.
    Connections {
        target: dialog
        function onVisibleChanged() { if (!dialog.visible && win.ownFlow) win.flow.cancelAuthenticationRequest() }
    }
    Connections {
        target: approver
        function onRunningChanged() { if (!approver.running) approver.record = null }
    }

    IpcHandler {
        target: "guardian"
        function open(): void { win.open() }
        function show(id: string): void { win.open(id) }
        function close(): void { dialog.visible = false }
        function state(): string {
            return JSON.stringify({ visible: dialog.visible, attention: win.attention, busy: win.busy, restartNeeded: win.restartNeeded,
                                    count: win.proposals.length, current: win.current ? { id: win.current.id, state: win.current.state } : null,
                                    approving: win.approving, polkitRegistered: polkit.isRegistered })
        }
    }
}
