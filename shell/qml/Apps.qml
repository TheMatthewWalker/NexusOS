import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Hyprland
import "settings"

// Apps: every installed app (and a search box, so it doubles as the app
// launcher), getting new ones from Flathub, system apps from nixpkgs to try
// first, and apt in the Ubuntu container.
// Flathub apps install for this person only (flatpak --user): no password
// and no system change. Software for the whole system goes through Guardian
// (/etc/nixos/local.nix); this window says so rather than doing it.
// Try before you install (feature 13, "Feat-TryIt"): Try it runs a nixpkgs
// app with `nexus try` in a throwaway bubblewrap sandbox (no access to your
// files; nothing kept when it closes); Keep it asks Core (/try/keep) for a
// normal Guardian install proposal, which still needs approval.
// Before a Flathub install, a sheet says who publishes the app, whether
// Flathub verified them, its licence, download size and permissions in plain
// words, with a warning for wide access. Every install and removal (Flathub
// here, apt through the apt command) is journalled with an Undo, and after a
// removal the app's leftover data is offered for deletion, never by default.
// apt is said plainly: the Ubuntu container runs as you and is not a sandbox;
// Install isolated uses a separate container that cannot see your files.
// Opened by Super+A, the top bar, Nexus, or `nexus-shell-ipc call apps open [tab]`.
NexusWindow {
    id: win
    property var theme
    property var bridge
    title: "Apps"
    visible: false
    implicitWidth: 1040; implicitHeight: 720
    minimumSize: Qt.size(760, 560)
    color: "#05071A"

    property string tab: "all"          // all | get | try | apt
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    function open(t) {
        if (["all", "get", "try", "apt"].includes(t)) tab = t;
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Apps)$"]);
        visible = true;
        query.forceActiveFocus();
    }

    IpcHandler {
        target: "apps"
        function open(tab: string): void { win.open(tab) }
        function search(text: string): void { win.open(""); query.text = text }
        function state(): string {
            return JSON.stringify({ visible: win.visible, tab: win.tab, flathub: win.flathub, results: win.results.map(r => r.id),
                                    installed: win.installedIds, busy: win.busy, message: win.message,
                                    sheet: win.sheetId, sheetLines: win.sheet ? win.sheet.lines : [], leftover: win.leftover,
                                    trial: win.trialAttr, trialRunning: trial.running, trialNote: win.trialNote })
        }
        // Try before you install: a nixpkgs app in a throwaway sandbox, and keeping it (a Guardian proposal).
        function tryApp(attr: string): bool { win.open("try"); return win.tryApp(attr) }
        function keepApp(attr: string): bool { return win.keepApp(attr) }
        function stopTrial(): void { win.stopTrial() }
    }

    // ---- installed apps (desktop entries) ----
    readonly property var allApps: DesktopEntries.applications.values
        .filter(e => !e.noDisplay && e.name)
        .sort((a, b) => a.name.localeCompare(b.name))
    function matches(e, q) {
        if (!q) return true;
        const hay = (e.name + " " + (e.genericName || "") + " " + (e.comment || "") + " " + (e.keywords || []).join(" ")).toLowerCase();
        return q.toLowerCase().split(/\s+/).every(w => hay.includes(w));
    }

    // ---- Flathub ----
    // Featured when nothing is searched: well-known apps people look for first.
    readonly property var featured: [
        { id: "com.spotify.Client", name: "Spotify", summary: "Music and podcasts" },
        { id: "com.discordapp.Discord", name: "Discord", summary: "Voice, video and text chat" },
        { id: "com.valvesoftware.Steam", name: "Steam", summary: "Games" },
        { id: "org.gimp.GIMP", name: "GIMP", summary: "Edit photos and images" },
        { id: "org.inkscape.Inkscape", name: "Inkscape", summary: "Vector graphics" },
        { id: "org.blender.Blender", name: "Blender", summary: "3D creation" },
        { id: "com.obsproject.Studio", name: "OBS Studio", summary: "Record and stream your screen" },
        { id: "org.mozilla.Thunderbird", name: "Thunderbird", summary: "Email and calendar" },
        { id: "org.signal.Signal", name: "Signal", summary: "Private messaging" },
        { id: "org.kde.krita", name: "Krita", summary: "Digital painting" },
        { id: "org.audacityteam.Audacity", name: "Audacity", summary: "Record and edit audio" },
        { id: "com.usebottles.bottles", name: "Bottles", summary: "Run Windows software" }
    ]
    property var results: []
    property var installedIds: []
    property bool flathub: true
    property string busy: ""           // the app id being installed or removed
    property string message: ""
    property string lastQuery: ""

    Process {
        id: listInstalled
        command: ["flatpak", "list", "--app", "--columns=application"]
        stdout: StdioCollector { onStreamFinished: win.installedIds = text.split("\n").map(s => s.trim()).filter(s => s) }
    }
    Process {
        id: searchProc
        stdout: StdioCollector {
            onStreamFinished: {
                // Tab-separated: application, name, description, version, remotes.
                const seen = new Set();
                win.results = text.split("\n").map(l => l.split("\t")).filter(f => f.length >= 3 && f[0].includes("."))
                    .filter(f => !seen.has(f[0]) && seen.add(f[0]))
                    .slice(0, 40).map(f => ({ id: f[0].trim(), name: f[1].trim(), summary: f[2].trim() }));
            }
        }
        stderr: StdioCollector { onStreamFinished: if (/No remote|remotes/i.test(text)) win.flathub = false }
    }
    Timer { id: searchDelay; interval: 400; onTriggered: win.searchFlathub() }
    function searchFlathub() {
        const q = query.text.trim();
        if (!q) { results = []; return }
        if (searchProc.running) { searchDelay.restart(); return }
        lastQuery = q;
        searchProc.command = ["flatpak", "search", "--columns=application,name,description", q];
        searchProc.running = true;
    }

    // ---- before installing: who makes it and what it may reach ----
    // Core reads the catalogue flatpak keeps on disk and the cached summary
    // (GET /apps/flathub/<id>, FlathubDetails.cs): publisher, verified or not,
    // licence, download size and the permissions in plain words. Nothing is
    // installed until the person presses Install on the sheet.
    readonly property var idPattern: /^[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,64}){2,6}$/
    property string sheetId: ""
    property var sheet: null           // the details, or null while they are read
    property string sheetNote: ""
    property int sheetCall: -1
    function requestInstall(id) {
        if (busy || !idPattern.test(id)) return;
        sheetId = id; sheet = null; sheetNote = "Reading what " + nameOf(id) + " can do…";
        if (!bridge || !bridge.online) { sheetNote = "Nexus Core is not running, so its publisher and permissions cannot be shown."; return }
        sheetCall = bridge.api("GET", "/apps/flathub/" + id);
    }
    function closeSheet() { sheetId = ""; sheet = null; sheetNote = "" }

    // ---- installing and removing, each journalled with an Undo ----
    // The change is reported to Core once it has happened (POST /apps/changes)
    // with what an Undo needs: the scope (--user or --system), remote, branch
    // and commit, read from `flatpak info`. Undo (Timeline or Nexus) comes back
    // here as perform(): a removal is undone by reinstalling in the same place
    // and going back to the same commit when Flathub still has it.
    property var changeInfo: ({})      // "Label" -> value, from `flatpak info`
    property var changeOpts: ({})
    readonly property var wordPattern: /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/
    Process {
        id: change
        property string verb: ""
        function line(text) {
            const t = text.trim();
            const m = /^@info\s+([A-Z][A-Za-z]{1,20}):\s+(\S.*)$/.exec(t);
            if (m) { const i = win.changeInfo; i[m[1]] = m[2].trim(); win.changeInfo = i; return }
            if (t.startsWith("@note ")) { win.message = t.slice(6, 200); return }
            if (t && !t.startsWith("@")) win.message = t.slice(0, 140);
        }
        stdout: SplitParser { onRead: line => change.line(line) }
        stderr: SplitParser { onRead: line => change.line(line) }
        onExited: code => {
            const id = win.busy, name = win.nameOf(id);
            win.message = code === 0 ? name + (change.verb === "install" ? " is installed. Find it in All apps." : " is removed.")
                                     : "Could not " + change.verb + " " + name + ". " + win.message;
            if (code === 0) win.report(change.verb, id, name);
            if (code === 0 && change.verb === "remove") win.checkLeftover(id, name, win.changeInfo.Installation === "system" ? "system" : "user");
            win.busy = "";
            listInstalled.running = true;
        }
    }
    // Move in's "Get these apps": [{ id, name, was, same }] from Core's fixed list; shown, never installed without a click.
    property var suggested: []
    function suggest(list) {
        suggested = (list || []).filter(a => a && /^[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,64}){2,6}$/.test(a.id)).slice(0, 40)
            .map(a => ({ id: a.id, name: String(a.name || a.id).slice(0, 80),
                         summary: a.same ? "On your old computer too" : "Instead of " + String(a.was || "").slice(0, 80) }));
        query.text = "";
        open("get");
    }
    function report(op, id, name) {
        if (!bridge || !bridge.online) return;
        const i = changeInfo, o = changeOpts;
        const scope = i.Installation === "system" || i.Installation === "user" ? i.Installation : (o.scope || "user");
        const body = { source: "flathub", op: op, id: id, name: String(name).slice(0, 80), scope: scope };
        if (wordPattern.test(i.Origin || "")) body.origin = i.Origin;
        if (wordPattern.test(i.Branch || "")) body.branch = i.Branch;
        if (/^[0-9a-f]{64}$/.test(i.Commit || "")) body.commit = i.Commit;
        if (/^[A-Za-z0-9][A-Za-z0-9 .+~:_()-]{0,63}$/.test(i.Version || "")) body.version = i.Version;
        if (o.undoes) body.undoes = o.undoes;
        bridge.api("POST", "/apps/changes", body);
    }
    function nameOf(id) { const r = results.concat(suggested, featured).find(x => x.id === id); return r ? r.name : (sheet && sheet.id === id && sheet.name) || changeOpts.name || id }
    // opts (from an Undo): { scope, origin, branch, commit, undoes, name }.
    function install(id, opts) {
        if (busy || !idPattern.test(id)) return;
        const o = opts || {};
        const scope = o.scope === "system" ? "system" : "user";
        const origin = wordPattern.test(o.origin || "") ? o.origin : "flathub";
        const branch = wordPattern.test(o.branch || "") ? o.branch : "";
        const commit = /^[0-9a-f]{64}$/.test(o.commit || "") ? o.commit : "";
        closeSheet();
        changeInfo = ({}); changeOpts = o;
        busy = id; message = "Installing " + nameOf(id) + "…"; change.verb = "install";
        // A per-person install needs Flathub in the person's own list of remotes.
        // One for everyone (only when undoing a removal of one) asks polkit.
        change.command = ["sh", "-c",
            "ref=\"$1\"; [ -z \"$4\" ] || ref=\"$1//$4\"; " +
            "if [ \"$2\" = system ]; then flatpak install --system --noninteractive -y \"$3\" \"$ref\"; " +
            "else flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo && " +
            "flatpak install --user --noninteractive -y \"$3\" \"$ref\"; fi || exit 1; " +
            "if [ -n \"$5\" ]; then flatpak update --\"$2\" --noninteractive -y --commit=\"$5\" \"$ref\" >/dev/null 2>&1 || " +
            "echo '@note The same version is no longer offered, so the current one is installed.'; fi; " +
            "LC_ALL=C flatpak info --\"$2\" \"$1\" | sed 's/^/@info /'",
            "sh", id, scope, origin, branch, commit];
        change.running = true;
    }
    function uninstall(id, opts) {
        if (busy || !idPattern.test(id)) return;
        const o = opts || {};
        changeInfo = ({}); changeOpts = o;
        busy = id; message = "Removing " + nameOf(id) + "…"; change.verb = "remove";
        // Apps installs for the person; one installed for everyone is removed there (polkit asks).
        // `flatpak info` first, so an Undo can reinstall the same version in the same place.
        change.command = ["sh", "-c",
            "s=\"$2\"; if [ -z \"$s\" ]; then if flatpak info --user \"$1\" >/dev/null 2>&1; then s=user; else s=system; fi; fi; " +
            "LC_ALL=C flatpak info --\"$s\" \"$1\" | sed 's/^/@info /'; " +
            "flatpak uninstall --\"$s\" --noninteractive -y \"$1\"",
            "sh", id, o.scope === "system" || o.scope === "user" ? o.scope : ""];
        change.running = true;
    }
    onVisibleChanged: if (visible) listInstalled.running = true

    // ---- after removing: the app's own data is offered, never deleted by default ----
    // Flatpak keeps an app's settings and files in ~/.var/app/<id>; keeping
    // them means a reinstall (or Undo) brings them back.
    property var leftover: null        // { id, name, size, scope }
    Process {
        id: leftoverCheck
        property var pending: null
        stdout: StdioCollector {
            onStreamFinished: {
                const size = text.trim().split(/\s/)[0];
                if (size && leftoverCheck.pending) win.leftover = Object.assign({ size: size }, leftoverCheck.pending);
            }
        }
    }
    function checkLeftover(id, name, scope) {
        leftover = null;
        leftoverCheck.pending = { id: id, name: name, scope: scope };
        leftoverCheck.command = ["sh", "-c", "[ -d \"$HOME/.var/app/$1\" ] && du -sh \"$HOME/.var/app/$1\"", "sh", id];
        leftoverCheck.running = true;
    }
    Process {
        id: leftoverDelete
        property var what: null
        onExited: code => {
            const w = leftoverDelete.what;
            win.message = code === 0 ? w.name + "'s data and settings are deleted." : "Could not delete " + w.name + "'s data.";
            if (code === 0 && win.bridge && win.bridge.online)
                win.bridge.api("POST", "/apps/changes", { source: "flathub", op: "delete-data", id: w.id, name: String(w.name).slice(0, 80), scope: w.scope });
        }
    }
    function deleteLeftover() {
        const w = leftover;
        if (!w || !idPattern.test(w.id)) return;
        leftover = null;
        leftoverDelete.what = w;
        leftoverDelete.command = ["sh", "-c", "rm -rf -- \"$HOME/.var/app/$1\"", "sh", w.id];
        leftoverDelete.running = true;
    }

    // ---- apt, in a terminal the person can see ----
    // The apt command reports each change to Core itself (nexus apps record);
    // NEXUS_UNDOES says the run is an Undo.
    readonly property string terminal: Quickshell.env("NEXUS_TERMINAL") || "kitty"
    readonly property var aptPattern: /^[a-z0-9][a-z0-9+.-]{0,99}$/
    function aptRun(op, pkg, version, isolated, undoes) {
        if (!["install", "remove"].includes(op) || !aptPattern.test(pkg || "")) return false;
        const target = /^[A-Za-z0-9][A-Za-z0-9.+~:_-]{0,99}$/.test(version || "") && op === "install" ? pkg + "=" + version : pkg;
        Quickshell.execDetached([terminal, "env", "NEXUS_UNDOES=" + (Number(undoes) > 0 ? String(Number(undoes)) : ""), "sh", "-c",
            "if [ \"$3\" = 1 ]; then apt --isolated \"$1\" \"$2\"; else apt \"$1\" \"$2\"; fi; echo; printf 'Press Enter to close. '; read -r _",
            "sh", op, target, isolated ? "1" : "0"]);
        return true;
    }

    // An instruction from Nexus's card or an Undo in the Timeline (Core: AppChanges.Inverse).
    function perform(p) {
        switch (p.op) {
        case "flathub.install":
            open("get"); install(p.appId, { scope: p.scope, origin: p.origin, branch: p.branch, commit: p.commit, undoes: p.undoes, name: p.name });
            return { ok: true, message: "Installing in Apps…" };
        case "flathub.remove":
            open("get"); uninstall(p.appId, { scope: p.scope, undoes: p.undoes, name: p.name });
            return { ok: true, message: "Removing in Apps…" };
        case "apt.install": case "apt.remove":
            return aptRun(p.op.slice(4), p.package, p.version, p.isolated === true, p.undoes)
                ? { ok: true, message: "Running apt in a terminal." } : { ok: false, message: "That is not an apt package name." };
        default: return { ok: false, message: "Apps cannot do that." };
        }
    }

    // ---- system apps, tried first (nixpkgs) ----
    // A curated list, so this works offline and needs no search service; any
    // other nixpkgs name can be typed. Trials fetch the app once online.
    readonly property var systemApps: [
        { attr: "krita", name: "Krita", summary: "Digital painting" },
        { attr: "gimp", name: "GIMP", summary: "Edit photos and images" },
        { attr: "inkscape", name: "Inkscape", summary: "Vector graphics" },
        { attr: "blender", name: "Blender", summary: "3D creation" },
        { attr: "audacity", name: "Audacity", summary: "Record and edit audio" },
        { attr: "kdePackages.kdenlive", name: "Kdenlive", summary: "Edit videos" },
        { attr: "obs-studio", name: "OBS Studio", summary: "Record and stream your screen" },
        { attr: "shotwell", name: "Shotwell", summary: "Organise photos" },
        { attr: "xournalpp", name: "Xournal++", summary: "Handwritten notes and marking up PDFs" },
        { attr: "zotero", name: "Zotero", summary: "References and citations" },
        { attr: "foliate", name: "Foliate", summary: "Read e-books" },
        { attr: "kdePackages.kate", name: "Kate", summary: "Text and code editor" },
        { attr: "gnome-chess", name: "Chess", summary: "Play chess" },
        { attr: "superTuxKart", name: "SuperTuxKart", summary: "Kart racing game" }
    ]
    // Same rule as nexus-try and Guardian: a plain nixpkgs attribute, nothing else.
    readonly property var attrPattern: /^[A-Za-z_][A-Za-z0-9_-]{0,63}(\.[A-Za-z_][A-Za-z0-9_-]{0,63}){0,3}$/
    function systemMatches() {
        const q = query.text.trim().toLowerCase();
        const found = systemApps.filter(a => !q || (a.name + " " + a.summary + " " + a.attr).toLowerCase().includes(q));
        if (q && attrPattern.test(query.text.trim()) && !found.some(a => a.attr === query.text.trim()))
            found.push({ attr: query.text.trim(), name: query.text.trim(), summary: "From nixpkgs, by its package name" });
        return found;
    }
    property string trialAttr: ""
    property string trialNote: ""
    property int keepId: -1
    function trialName(attr) { const a = systemApps.find(x => x.attr === attr); return a ? a.name : attr }
    function tryApp(attr) {
        if (!attrPattern.test(attr) || trial.running) return false;
        trialAttr = attr; trialNote = "Getting " + trialName(attr) + " ready. The first time, it downloads (nothing is installed)…";
        trial.command = [Quickshell.env("NEXUS_CLI") || "nexus", "try", attr];
        trial.running = true;
        return true;
    }
    function stopTrial() { if (trial.running) { trial.running = false; trialNote = "Closed. Everything the trial made is gone." } }
    function keepApp(attr) {
        if (!attrPattern.test(attr)) return false;
        if (!bridge || !bridge.online) { trialNote = "Nexus Core is not running, so keeping an app cannot be asked for now."; return false }
        trialAttr = attr; trialNote = "Asking Guardian to install " + trialName(attr) + "…";
        keepId = bridge.api("POST", "/try/keep", { attr: attr });
        return true;
    }
    Process {
        id: trial
        stderr: SplitParser { onRead: line => { if (line.trim()) win.trialNote = line.trim().replace(/^nexus-try: /, "").slice(0, 200) } }
        onExited: code => {
            if (code === 0) win.trialNote = win.trialName(win.trialAttr) + " closed. Nothing was kept.";
            else if (!/^Closed/.test(win.trialNote)) win.trialNote = "The trial stopped: " + win.trialNote;
        }
    }
    Connections {
        target: win.bridge
        ignoreUnknownSignals: true
        function onResult(id, type, data) {
            if (id === win.keepId) win.trialNote = "Sent for approval: install " + win.trialName(win.trialAttr) + " for every account. " +
                "Nothing is installed until you approve it as a system change; it then installs on the next trial boot and can be rolled back.";
            else if (id === win.sheetCall && data && data.id === win.sheetId) { win.sheet = data; win.sheetNote = "" }
        }
        function onFailed(id, message) {
            if (id === win.keepId) win.trialNote = "Keeping it did not work: " + message;
            else if (id === win.sheetCall) win.sheetNote = "Its details could not be read: " + message;
        }
    }

    // ---- the window ----
    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#0B0F24" }
            GradientStop { position: 1; color: "#020309" }
        }
    }
    Column {
        x: 32; y: 26; width: parent.width - 64
        spacing: 16
        Row {
            width: parent.width; spacing: 16
            Text { text: "Apps"; color: win.theme.ink; font.pixelSize: 26; font.weight: 600; anchors.verticalCenter: parent.verticalCenter }
            Choice {
                theme: win.theme; name: "Show"; current: win.tab; anchors.verticalCenter: parent.verticalCenter
                options: [{ value: "all", label: "All apps" }, { value: "get", label: "Get apps" }, { value: "try", label: "System apps" },
                          { value: "apt", label: "Ubuntu (apt)" }]
                onChose: v => { win.tab = v; if (v === "get") win.searchFlathub() }
            }
        }
        Rectangle {
            visible: win.tab !== "apt"
            width: parent.width; height: 46; radius: 14
            color: win.theme.glass; border.color: query.activeFocus ? win.theme.focus : win.theme.glassBorder
            Image { x: 14; anchors.verticalCenter: parent.verticalCenter; width: 18; height: 18; sourceSize: Qt.size(36, 36); source: "icons/search.svg" }
            TextInput {
                id: query
                x: 42; width: parent.width - 56; anchors.verticalCenter: parent.verticalCenter
                color: win.theme.ink; font.pixelSize: 14; clip: true
                Text { visible: !query.text; color: win.theme.inkSecondary; font.pixelSize: 14
                       text: win.tab === "get" ? "Search Flathub, e.g. spotify" : win.tab === "try" ? "Find a system app, or type its nixpkgs name"
                             : "Search your apps" }
                onTextEdited: if (win.tab === "get") searchDelay.restart()
                // Enter opens the first app, like a launcher.
                onAccepted: {
                    if (win.tab === "all") {
                        const first = win.allApps.find(e => win.matches(e, text.trim()));
                        if (first) { first.execute(); win.visible = false; text = "" }
                    } else if (win.tab === "try") { const first = win.systemMatches()[0]; if (first) win.tryApp(first.attr) }
                    else win.searchFlathub();
                }
                Keys.onEscapePressed: { if (text) text = ""; else win.visible = false }
                Accessible.role: Accessible.EditableText
                Accessible.name: "Search apps"
            }
        }
        Text {
            visible: win.message !== ""
            width: parent.width; elide: Text.ElideRight; textFormat: Text.PlainText
            text: win.message; color: win.theme.inkSecondary; font.pixelSize: 13
        }
    }

    // All apps: a grid of every installed app.
    GridView {
        id: grid
        visible: win.tab === "all"
        x: 24; y: 150; width: parent.width - 48; height: parent.height - 170
        clip: true
        cellWidth: Math.floor(width / Math.max(1, Math.floor(width / 132))); cellHeight: 120
        model: win.allApps.filter(e => win.matches(e, query.text.trim()))
        delegate: Item {
            required property var modelData
            width: grid.cellWidth; height: grid.cellHeight
            Rectangle {
                anchors { fill: parent; margins: 6 }
                radius: 14; color: tileHover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.18) : "transparent"
                Column {
                    anchors.centerIn: parent; spacing: 8; width: parent.width - 12
                    Image {
                        anchors.horizontalCenter: parent.horizontalCenter
                        width: 48; height: 48; sourceSize: Qt.size(96, 96)
                        source: modelData.icon ? Quickshell.iconPath(modelData.icon, true) : ""
                    }
                    Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; elide: Text.ElideRight
                           text: modelData.name; color: win.theme.ink; font.pixelSize: 12 }
                }
                HoverHandler { id: tileHover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: { modelData.execute(); win.visible = false } }
                Accessible.role: Accessible.Button
                Accessible.name: "Open " + modelData.name
            }
        }
    }

    // Get apps: Flathub.
    ListView {
        id: storeList
        visible: win.tab === "get"
        x: 32; y: 160; width: parent.width - 64; height: parent.height - 180
        clip: true; spacing: 8
        header: Text {
            width: storeList.width; bottomPadding: 10; wrapMode: Text.Wrap
            text: !win.flathub ? "Flathub is not available yet: it is added once this computer has been online."
                : query.text.trim() ? "From Flathub. Apps install for you only, with no password."
                : win.suggested.length ? "Get these apps: matched to the programs on your old computer (Move in). Nothing installs until you press Install."
                : "Popular on Flathub. Search above for anything else. Apps install for you only, with no password."
            color: win.theme.inkSecondary; font.pixelSize: 13
        }
        model: query.text.trim() ? win.results : win.suggested.length ? win.suggested : win.featured
        delegate: SettingRow {
            required property var modelData
            readonly property bool installed: win.installedIds.includes(modelData.id)
            theme: win.theme; width: storeList.width
            label: modelData.name; detail: modelData.summary + " · " + modelData.id
            Row {
                spacing: 8
                Button { theme: win.theme; visible: installed; label: "Open"
                         onClicked: { Quickshell.execDetached(["flatpak", "run", modelData.id]); win.visible = false } }
                Button { theme: win.theme; visible: installed; danger: true; label: win.busy === modelData.id ? "Removing…" : "Remove"
                         enabled: win.busy === ""; onClicked: win.uninstall(modelData.id) }
                Button { theme: win.theme; visible: !installed; primary: true; label: win.busy === modelData.id ? "Installing…" : "Install"
                         enabled: win.busy === "" && win.flathub; onClicked: win.requestInstall(modelData.id) }
            }
        }
    }

    // The install sheet: what the app is and what it may reach, then Install or Cancel.
    // After a removal, the same place offers to delete the app's leftover data.
    Rectangle {
        id: sheetPanel
        visible: win.sheetId !== "" || win.leftover !== null
        z: 5
        x: 32; width: parent.width - 64
        height: Math.min(sheetCol.implicitHeight + 32, parent.height - 200)
        anchors.bottom: parent.bottom; anchors.bottomMargin: 20
        radius: 16; color: "#0E1630"
        border.color: win.sheet && win.sheet.warning ? "#FFA65C" : win.theme.glassBorder
        Flickable {
            anchors { fill: parent; margins: 16 }
            contentHeight: sheetCol.implicitHeight; clip: true
            Column {
                id: sheetCol
                width: parent.width; spacing: 8
                // Install sheet.
                Text { visible: win.sheetId !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: "Install " + win.nameOf(win.sheetId) + " (" + win.sheetId + ")"
                       color: win.theme.ink; font.pixelSize: 16; font.weight: 600 }
                Text { visible: win.sheetId !== "" && win.sheetNote !== ""; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: win.sheetNote; color: win.theme.inkSecondary; font.pixelSize: 13 }
                Text { visible: win.sheetId !== "" && !!(win.sheet && win.sheet.warning); width: parent.width; wrapMode: Text.Wrap
                       textFormat: Text.PlainText; text: win.sheet && win.sheet.warning ? win.sheet.warning : ""
                       color: "#FFC48F"; font.pixelSize: 13; font.weight: 500 }
                Repeater {
                    model: win.sheetId !== "" && win.sheet ? win.sheet.lines : []
                    delegate: Text {
                        required property var modelData
                        width: sheetCol.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                        text: "· " + modelData; color: win.theme.inkSecondary; font.pixelSize: 13
                    }
                }
                Row {
                    visible: win.sheetId !== ""
                    spacing: 8
                    Button { theme: win.theme; primary: true; label: "Install, just for me"; enabled: win.busy === "" && win.flathub
                             onClicked: win.install(win.sheetId) }
                    Button { theme: win.theme; label: "Cancel"; onClicked: win.closeSheet() }
                }
                // Leftover data after a removal: offered, never deleted by default.
                Text { visible: win.sheetId === "" && win.leftover !== null; width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: win.leftover ? win.leftover.name + "'s data and settings are still in your home folder (~/.var/app/" + win.leftover.id +
                                            ", " + win.leftover.size + "). Keeping them means reinstalling it, or Undo, brings them back. " +
                                            "Deleting them cannot be undone." : ""
                       color: win.theme.ink; font.pixelSize: 13 }
                Row {
                    visible: win.sheetId === "" && win.leftover !== null
                    spacing: 8
                    Button { theme: win.theme; label: "Keep them"; onClicked: win.leftover = null }
                    Button { theme: win.theme; danger: true; label: "Delete its data"; onClicked: win.deleteLeftover() }
                }
            }
        }
    }

    // System apps: try first in a sandbox, keep through Guardian.
    Column {
        id: tryHead
        visible: win.tab === "try"
        x: 32; y: 160; width: parent.width - 64
        spacing: 10
        Text {
            width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 13
            text: "Apps from NixOS itself. Try it opens the app straight away in a sandbox: it cannot see your files, " +
                  "and nothing is kept when you close it. Keep it installs it for every account, as a system change that waits for your approval."
        }
        // The running (or last) trial.
        Rectangle {
            visible: win.trialAttr !== ""
            width: parent.width; height: trialCol.implicitHeight + 28; radius: 14
            color: "#0E1630"; border.color: trial.running ? "#FFA65C" : win.theme.glassBorder
            Column {
                id: trialCol
                x: 16; y: 14; width: parent.width - 32; spacing: 8
                Row {
                    spacing: 10
                    Rectangle {
                        visible: trial.running
                        width: trialTag.implicitWidth + 20; height: 24; radius: 12; color: "#2A1A0C"; border.color: "#FFA65C"
                        Text { id: trialTag; anchors.centerIn: parent; text: "Trial"; color: "#FFC48F"; font.pixelSize: 12; font.weight: 500 }
                    }
                    Text { anchors.verticalCenter: parent.verticalCenter; text: win.trialName(win.trialAttr); color: win.theme.ink; font.pixelSize: 14; font.weight: 500 }
                    Text { anchors.verticalCenter: parent.verticalCenter; visible: trial.running
                           text: "Sandboxed · throwaway home folder"; color: win.theme.inkSecondary; font.pixelSize: 12 }
                }
                Text { width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText
                       text: win.trialNote; color: win.theme.inkSecondary; font.pixelSize: 12 }
                Row {
                    spacing: 8
                    Button { theme: win.theme; visible: trial.running; label: "Close and discard"; onClicked: win.stopTrial() }
                    Button { theme: win.theme; label: "Keep it · Review"; onClicked: win.keepApp(win.trialAttr) }
                }
            }
        }
        Text {
            width: parent.width; wrapMode: Text.Wrap; color: win.theme.muted; font.pixelSize: 12
            text: "A trial can use its own window, keyboard, pointer, sound and the network; no camera, microphone, other apps or your files. " +
                  "To hand it one file: nexus try --file <file> <name>"
        }
    }
    ListView {
        id: tryList
        visible: win.tab === "try"
        x: 32; y: tryHead.y + tryHead.height + 12; width: parent.width - 64; height: parent.height - y - 20
        clip: true; spacing: 8
        model: win.systemMatches()
        delegate: SettingRow {
            required property var modelData
            theme: win.theme; width: tryList.width
            label: modelData.name; detail: modelData.summary + " · " + modelData.attr
            Row {
                spacing: 8
                Button { theme: win.theme; label: "Keep it"; onClicked: win.keepApp(modelData.attr) }
                Button { theme: win.theme; primary: true; label: trial.running && win.trialAttr === modelData.attr ? "Running…" : "Try it"
                         enabled: !trial.running; onClicked: win.tryApp(modelData.attr) }
            }
        }
    }

    // Ubuntu (apt). Said plainly: the Ubuntu container is not a sandbox.
    Column {
        visible: win.tab === "apt"
        x: 32; y: 110; width: parent.width - 64
        spacing: 10
        SettingRow {
            theme: win.theme; label: "apt, in an Ubuntu container"
            detail: "NexusOS is built on NixOS, which has no apt of its own. The apt command runs apt inside Ubuntu 24.04. " +
                    "Apps you install there are added to All apps. No password is needed."
            Button { theme: win.theme; label: "Open a terminal"; onClicked: Quickshell.execDetached([win.terminal]) }
        }
        Rectangle {
            width: parent.width; height: aptWarn.implicitHeight + 24; radius: 12
            color: "#2A1A0C"; border.color: "#FFA65C"
            Text {
                id: aptWarn
                x: 16; y: 12; width: parent.width - 32; wrapMode: Text.Wrap; color: "#FFC48F"; font.pixelSize: 13
                text: "Ubuntu software runs as you and can read your home folder. The Ubuntu container shares it (and can see the " +
                      "rest of this computer's files), so it is not a sandbox: install only software you trust. " +
                      "For anything else, use Install isolated: a separate container with its own home folder that cannot see your files " +
                      "(command-line tools only; run them with apt --isolated run <name>)."
            }
        }
        SettingRow {
            theme: win.theme; label: "Install a package"
            detail: "Opens a terminal so you see what apt does. Installs and removals are in Settings, Timeline, where they can be undone."
            Row {
                spacing: 8
                Rectangle {
                    width: 180; height: 34; radius: 10; color: win.theme.glass
                    border.color: aptName.activeFocus ? win.theme.focus : win.theme.glassBorder
                    TextInput {
                        id: aptName
                        x: 10; width: parent.width - 20; anchors.verticalCenter: parent.verticalCenter
                        color: win.theme.ink; font.pixelSize: 13; clip: true
                        validator: RegularExpressionValidator { regularExpression: /[a-z0-9][a-z0-9+.-]{0,99}/ }
                        Text { visible: !aptName.text; text: "package, e.g. htop"; color: win.theme.inkSecondary; font.pixelSize: 13 }
                        Accessible.role: Accessible.EditableText
                        Accessible.name: "apt package name"
                    }
                }
                Button { theme: win.theme; label: "Install isolated"; enabled: win.aptPattern.test(aptName.text)
                         onClicked: win.aptRun("install", aptName.text, "", true, 0) }
                Button { theme: win.theme; primary: true; label: "Install"; enabled: win.aptPattern.test(aptName.text)
                         onClicked: win.aptRun("install", aptName.text, "", false, 0) }
            }
        }
        SettingRow {
            theme: win.theme; label: "Examples"
            detail: "apt install htop        apt search ffmpeg        apt remove htop        apt upgrade        apt shell (a terminal inside Ubuntu)        " +
                    "apt --isolated install X (no access to your files)"
        }
        SettingRow {
            theme: win.theme; label: "Software for the whole system"
            detail: "Drivers, services and software every account should have are added to NexusOS itself, in " +
                    "/etc/nixos/local.nix, and go through Guardian: you see the change and approve it, and it can be rolled back. " +
                    "To try a program first without installing it, run: nexus try <name>"
        }
    }
}
