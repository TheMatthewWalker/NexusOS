import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/storage.mjs" as S
import "../lib/privacy.mjs" as Priv

// Settings, Privacy and AI: today at a glance (feature 12, Privacy
// dashboard: what Nexus read, what left the computer, which apps used the
// microphone or camera, and Forget today), which model answers, what may
// leave the computer, conversation history, and the notes Nexus reads (the
// person's own "training documents": AssistantContext.cs adds them to every question).
// App permissions: what each Flathub app may reach (nexus-permissions).
Column {
    id: page
    property var theme
    property var host
    readonly property var bridge: host.bridge
    width: parent ? parent.width : 600
    spacing: 10

    property var providers: []
    property string selected: ""
    property var status: null
    property string message: ""
    property bool confirmClear: false
    property int providersId: -1
    property int statusId: -1
    property int selectId: -1
    property int clearId: -1
    property var today: null
    property var todayEntries: []
    property int todayId: -1
    property int todayListId: -1
    property int forgetId: -1
    // The large mixture-of-experts model (Core's /model/large), where this computer has one.
    property var large: null
    property int largeId: -1
    property int largeUseId: -1
    readonly property bool largeOn: !!large && large.selected
    function largeDetail() {
        const l = large, d = l.download;
        if (!l.downloaded) {
            if (d && d.state === "downloading") return "Downloading " + l.name + ": " + d.percent + "% of " + l.sizeGb + " GB.";
            return ((d && d.message) || (l.name + " downloads once this computer is online.")) + (d && d.note ? " " + d.note : "");
        }
        const how = l.name + " runs " + l.where + " and needs about " + l.memoryGb + " GB of free memory.";
        if (l.selected && l.blocked) return l.blocked + " The standard model answers meanwhile.";
        const which = d && d.note ? " " + d.note : "";
        return (l.selected ? how + (l.running ? " Loaded." : " It loads with your next question.")
                           : how + " Smarter than the standard model, and slower.") + which;
    }
    // The very large model (Core's /model/colibri): colibri reads most of the model
    // from the disk, so it runs models far bigger than memory. Nothing downloads
    // until the person turns it on here.
    property var colibri: null
    property int colibriId: -1
    property int colibriActId: -1
    property string colibriPick: ""
    property bool confirmRemove: false
    readonly property var colibriModels: colibri && colibri.models ? colibri.models : []
    readonly property var colibriCurrent: colibriModels.find(m => m.id === (colibri ? colibri.model : "")) || null
    function colibriName() { return colibriCurrent ? colibriCurrent.name : (colibri ? colibri.model : "") }
    function colibriDetail() {
        const c = colibri, d = c.download;
        if (!c.cpuSupported) return "This processor is too old for it (it needs AVX2, from about 2013 on).";
        if (!c.wanted && !c.downloaded) {
            const m = colibriModels.find(x => x.id === (page.colibriPick || c.model));
            return "Runs models far bigger than this computer's memory by reading most of the model from the disk. Choose one to download"
                + (m ? ": " + m.name + ", " + m.downloadGb + " GB. " + m.use + (m.fits ? "." : ". " + m.why + ".") : ".");
        }
        if (!c.downloaded) {
            if (d && d.state === "downloading") return "Downloading " + colibriName() + ": " + d.percent + "% of "
                + (d.total / 1e9).toFixed(1) + " GB. It goes on after a restart.";
            return (d && d.message) || ("Downloading " + colibriName() + " shortly.");
        }
        if (c.selected && c.blocked) return c.blocked + " The standard model answers meanwhile.";
        return colibriName() + (c.selected ? (c.running ? " answers your questions." : " loads with your next question; the first answer is slow.")
                                           : " is downloaded. Much bigger than the standard model, and slower.");
    }
    property bool confirmForget: false
    readonly property string todayDate: Qt.formatDate(new Date(), "yyyy-MM-dd")
    readonly property string notesDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos/assistant"
    readonly property var labels: ({ "local": "On this computer (Ollama)", "llama-cpp": "On this computer (large model)",
                                     "colibri": "On this computer (very large model)", "openai-compatible": "Cloud model" })
    function refresh() {
        if (!bridge.online) return;
        providersId = bridge.send("providers");
        statusId = bridge.send("status");
        largeId = bridge.api("GET", "/model/large");
        colibriId = bridge.api("GET", "/model/colibri");
        todayId = bridge.api("GET", "/journal/today/summary");
        const midnight = new Date(); midnight.setHours(0, 0, 0, 0);
        todayListId = bridge.api("GET", "/journal?limit=60&since=" + encodeURIComponent(midnight.toISOString()));
    }
    Component.onCompleted: { refresh(); permApps.running = true }
    Connections {
        target: page.bridge
        function onOnlineChanged() { page.refresh() }
        function onResult(id, type, data) {
            if (id === page.providersId) { page.providers = data.providers || []; page.selected = data.selected || "" }
            else if (id === page.statusId) page.status = data;
            else if (id === page.selectId) { page.selected = data.selected; page.statusId = page.bridge.send("status") }
            else if (id === page.clearId) { page.confirmClear = false; page.message = "Conversation history erased." }
            else if (id === page.largeId || id === page.largeUseId) {
                page.large = data && data.configured ? data : null;
                if (id === page.largeUseId) { page.providersId = page.bridge.send("providers"); page.statusId = page.bridge.send("status") }
            }
            else if (id === page.colibriId || id === page.colibriActId) {
                page.colibri = data && data.configured ? data : null;
                page.confirmRemove = false;
                if (id === page.colibriActId) { page.providersId = page.bridge.send("providers"); page.statusId = page.bridge.send("status") }
            }
            else if (id === page.todayId) page.today = data;
            else if (id === page.todayListId) page.todayEntries = (data || []).filter(e => ["ai-read", "egress", "device"].includes(e.kind));
            else if (id === page.forgetId) { page.confirmForget = false; page.message = "Today's record is erased."; page.refresh() }
        }
        function onFailed(id, message) {
            if ([page.providersId, page.statusId, page.selectId, page.clearId, page.todayId, page.todayListId, page.forgetId, page.largeUseId, page.colibriActId].includes(id)) page.message = message;
        }
    }

    PageHeader { theme: page.theme; title: "Privacy and AI"
                 summary: "Nexus runs on this computer. Nothing you ask leaves it unless you turn on a cloud model." }

    // Today, at a glance.
    Row {
        width: parent.width; spacing: 10
        Repeater {
            model: [
                { n: page.today ? page.today.aiReads : "–", label: "things Nexus read" },
                { n: page.today ? page.today.leftTheComputer : "–", label: "requests left this computer" },
                { n: page.today ? page.today.deviceUses : "–", label: "uses of microphone or camera" }
            ]
            delegate: Rectangle {
                required property var modelData
                width: (page.width - 20) / 3; height: 84; radius: 12; color: page.theme.chip
                Column {
                    anchors.centerIn: parent; spacing: 4
                    Text { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.n; color: page.theme.ink; font.pixelSize: 26; font.weight: 600 }
                    Text { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.label; color: page.theme.inkSecondary; font.pixelSize: 12 }
                }
            }
        }
    }
    Repeater {
        model: page.todayEntries.slice(0, 12)
        delegate: Text {
            required property var modelData
            width: page.width; elide: Text.ElideRight; textFormat: Text.PlainText; leftPadding: 4
            text: Qt.formatTime(new Date(modelData.time), "HH:mm") + "  " + modelData.summary
            color: modelData.kind === "egress" ? page.theme.error : page.theme.inkSecondary; font.pixelSize: 12
        }
    }
    SettingRow {
        theme: page.theme; label: "Forget today"; highlighted: page.host.highlight === "forget"
        detail: page.confirmForget ? "Erase today's record of what Nexus read, what left the computer and device use? This cannot be undone."
              : "Erases today's record. Your files and settings stay as they are."
        Row {
            spacing: 8
            Button { theme: page.theme; visible: !page.confirmForget; label: "Forget today"; enabled: page.bridge.online
                     onClicked: page.confirmForget = true }
            Button { theme: page.theme; visible: page.confirmForget; label: "Cancel"; onClicked: page.confirmForget = false }
            Button { theme: page.theme; visible: page.confirmForget; primary: true; danger: true; label: "Erase today"
                     onClicked: page.forgetId = page.bridge.api("DELETE", "/journal?day=" + page.todayDate) }
        }
    }

    SettingRow {
        theme: page.theme; label: "Model"; highlighted: page.host.highlight === "model"
        detail: !page.bridge.online ? (page.bridge.offlineReason || "Nexus Core is not running.")
              : !page.status ? "Checking…"
              : page.status.model + " · " + (page.status.available === false ? "not ready (is Ollama running, and the model downloaded?)"
                                                                                : "ready")
    }
    // The large model: a mixture of experts, of which only a few work on each word,
    // so it runs with its experts in ordinary memory on a computer a model its size would not fit.
    SettingRow {
        visible: page.large !== null
        theme: page.theme; label: "Large model"; highlighted: page.host.highlight === "largemodel"
        detail: page.large ? page.largeDetail() : ""
        Switch { theme: page.theme; name: "Large model"; checked: page.largeOn
                 enabled: !!page.large && (page.large.downloaded || page.largeOn)
                 onToggled: page.largeUseId = page.bridge.api("POST", "/model/large/use", { on: !page.largeOn }) }
    }
    // While it downloads or loads, the row keeps itself current.
    Timer { interval: 5000; repeat: true
            running: page.visible && !!page.large && (!page.large.downloaded || (page.large.selected && !page.large.running))
            onTriggered: if (page.bridge.online) page.largeId = page.bridge.api("GET", "/model/large") }
    SettingRow {
        visible: page.colibri !== null
        theme: page.theme; label: "Very large model"; highlighted: page.host.highlight === "verylargemodel"
        detail: page.colibri ? page.colibriDetail() : ""
        Column {
            spacing: 8
            // Before it is chosen: which model, from those this computer can hold.
            Choice { theme: page.theme; name: "Very large model to download"
                     visible: !!page.colibri && page.colibri.cpuSupported && !page.colibri.wanted && !page.colibri.downloaded
                              && page.colibriModels.length > 1
                     current: page.colibriPick || (page.colibri ? page.colibri.model : "")
                     options: page.colibriModels.filter(m => m.fits).map(m => ({ value: m.id, label: m.name + " (" + m.downloadGb + " GB)" }))
                     onChose: v => page.colibriPick = v }
            Row {
                spacing: 8
                Button { theme: page.theme; label: "Download"; primary: true
                         visible: !!page.colibri && page.colibri.cpuSupported && !page.colibri.wanted && !page.colibri.downloaded
                         enabled: page.bridge.online && (!page.colibriCurrent || page.colibriModels.some(m => m.id === (page.colibriPick || page.colibri.model) && m.fits))
                         onClicked: page.colibriActId = page.bridge.api("POST", "/model/colibri/want",
                                        { on: true, model: page.colibriPick || page.colibri.model }) }
                Button { theme: page.theme; label: "Stop the download"
                         visible: !!page.colibri && page.colibri.wanted && !page.colibri.downloaded
                         onClicked: page.colibriActId = page.bridge.api("POST", "/model/colibri/want", { on: false }) }
                Switch { theme: page.theme; name: "Use the very large model"
                         visible: !!page.colibri && page.colibri.downloaded; checked: !!page.colibri && page.colibri.selected
                         onToggled: page.colibriActId = page.bridge.api("POST", "/model/colibri/use", { on: !page.colibri.selected }) }
                Button { theme: page.theme; label: "Remove"; visible: !!page.colibri && page.colibri.downloaded && !page.confirmRemove
                         onClicked: page.confirmRemove = true }
                Button { theme: page.theme; label: "Cancel"; visible: page.confirmRemove; onClicked: page.confirmRemove = false }
                Button { theme: page.theme; label: "Delete " + page.colibriName(); visible: page.confirmRemove; primary: true; danger: true
                         onClicked: page.colibriActId = page.bridge.api("DELETE", "/model/colibri") }
            }
        }
    }
    Timer { interval: 5000; repeat: true
            running: page.visible && !!page.colibri && ((page.colibri.wanted && !page.colibri.downloaded) || (page.colibri.selected && !page.colibri.running))
            onTriggered: if (page.bridge.online) page.colibriId = page.bridge.api("GET", "/model/colibri") }
    SettingRow {
        theme: page.theme; label: "Where answers come from"; highlighted: page.host.highlight === "provider"
        visible: page.providers.length > 0
        detail: page.selected === "openai-compatible"
            ? "Questions are sent to the cloud provider. Each request is recorded in the activity journal."
            : "Questions stay on this computer."
        Choice { theme: page.theme; name: "Where answers come from"; current: page.selected
                 options: page.providers.map(p => ({ value: p, label: page.labels[p] || p }))
                 onChose: v => page.selectId = page.bridge.send("select", { name: v }) }
    }
    SettingRow {
        theme: page.theme; label: "Your notes for Nexus"; highlighted: page.host.highlight === "notes"
        detail: "Text or Markdown files in " + page.notesDir.replace(Quickshell.env("HOME"), "~") +
                " are read with every question: who you are, how you like answers, what you use this computer for."
        Button { theme: page.theme; label: "Open folder"; onClicked: page.host.openFolder(page.notesDir) }
    }
    SettingRow {
        theme: page.theme; label: "Conversation history"; highlighted: page.host.highlight === "history"
        detail: page.confirmClear ? "Erase every saved conversation? This cannot be undone."
              : "Kept on this computer so follow-up questions make sense, and erased after 30 days."
        Row {
            spacing: 8
            Button { theme: page.theme; visible: !page.confirmClear; danger: true; label: "Erase history"
                     enabled: page.bridge.online; onClicked: page.confirmClear = true }
            Button { theme: page.theme; visible: page.confirmClear; label: "Cancel"; onClicked: page.confirmClear = false }
            Button { theme: page.theme; visible: page.confirmClear; primary: true; danger: true; label: "Erase"
                     onClicked: page.clearId = page.bridge.send("clear-history") }
        }
    }
    SettingRow {
        theme: page.theme; label: "System changes"; highlighted: page.host.highlight === "guardian"
        detail: "Changes to the system itself, such as allowing cloud models for everyone, installing software or " +
                "turning on SSH, go through Guardian: you see the change, approve it, and it can be rolled back."
    }
    Text {
        visible: page.message !== ""
        width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
        text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13
    }

    // ---- app permissions (Flathub apps) ----
    property var flatApps: []
    property string permApp: ""
    property var perms: []
    property string permNote: ""
    Process { id: permApps; command: ["nexus-permissions", "apps"]
              stdout: StdioCollector { onStreamFinished: { const v = S.parse(text); page.flatApps = Array.isArray(v) ? v : [] } } }
    Process { id: permShow
              stdout: StdioCollector { onStreamFinished: { const v = S.parse(text); page.perms = v.permissions || []; if (v.error) page.permNote = v.error } } }
    Process {
        id: permSet
        stdout: StdioCollector { id: permOut }
        onExited: { const v = S.parse(permOut.text); page.permNote = v.error || v.message || ""; page.showPerms(page.permApp) }
    }
    function showPerms(app) {
        permApp = app; perms = [];
        if (!app) return;
        permShow.command = ["nexus-permissions", "show", app]; permShow.running = true;
    }
    function setPerm(args) {
        if (permSet.running) return;
        permSet.command = ["nexus-permissions"].concat(args); permSet.running = true;
    }

    SettingRow {
        theme: page.theme; label: "App permissions"; highlighted: page.host.highlight === "permissions"
        detail: page.flatApps.length === 0
            ? "Apps from Flathub show here, with what each may reach: the internet, your files, the camera and devices."
            : "What each app from Flathub may reach. A change is for you only and applies the next time the app starts."
    }
    Flow {
        width: page.width; spacing: 6
        Repeater {
            model: page.flatApps
            delegate: Button {
                required property var modelData
                theme: page.theme; label: modelData.name; primary: page.permApp === modelData.id
                onClicked: page.showPerms(page.permApp === modelData.id ? "" : modelData.id)
            }
        }
    }
    Text { visible: page.permNote !== "" && page.permApp !== ""; width: page.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
           text: page.permNote; color: page.theme.inkSecondary; font.pixelSize: 12 }
    Repeater {
        model: page.permApp ? page.perms : []
        delegate: SettingRow {
            required property var modelData
            theme: page.theme; label: modelData.label
            detail: modelData.asked ? "" : "The app did not ask for this."
            Switch { theme: page.theme; name: modelData.label; checked: modelData.on
                     onToggled: page.setPerm(["set", page.permApp, modelData.kind, modelData.on ? "off" : "on"]) }
        }
    }
    Button { visible: page.permApp !== ""; theme: page.theme; label: "Back to what the app asked for"
             onClicked: page.setPerm(["reset", page.permApp]) }

    // Virus scanning on demand (ClamAV, after it is turned on through Guardian).
    property bool scanOn: false
    property string scanNote: ""
    Process { running: true; command: ["sh", "-c", "command -v nexus-scan"]; onExited: (code) => page.scanOn = code === 0 }
    Process {
        id: scanner
        stdout: StdioCollector { onStreamFinished: page.scanNote = text.trim() }
    }
    Process {
        id: scanPicker
        command: ["zenity", "--file-selection", "--directory", "--title=Choose a folder to scan"]
        stdout: StdioCollector { onStreamFinished: { const f = text.trim(); if (f.startsWith("/")) page.scan(f) } }
    }
    function scan(folder) {
        if (scanner.running) return;
        scanNote = "Scanning…";
        scanner.command = ["nexus-scan"].concat(folder ? [folder] : []);
        scanner.running = true;
    }
    SettingRow {
        theme: page.theme; label: "Scan for viruses"; highlighted: page.host.highlight === "virusscan"
        detail: page.scanNote || (page.scanOn
            ? "Checks Downloads, or a folder you choose, with ClamAV. Nothing is deleted without you; nothing scans in the background."
            : "Viruses for Linux are rare, but files you pass on to Windows computers can carry them. Turning this on adds ClamAV and keeps its definitions up to date.")
        Row {
            spacing: 8
            Button { visible: !page.scanOn; theme: page.theme; label: "Turn on…"; onClicked: page.host.proposeSystem(Priv.SCAN_PLAN) }
            Button { visible: page.scanOn; theme: page.theme; label: "Scan a folder…"; enabled: !scanner.running && !scanPicker.running
                     onClicked: scanPicker.running = true }
            Button { visible: page.scanOn; theme: page.theme; primary: true; label: scanner.running ? "Scanning…" : "Scan Downloads"
                     enabled: !scanner.running; onClicked: page.scan("") }
        }
    }
}
