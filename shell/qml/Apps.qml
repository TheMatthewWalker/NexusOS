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
// Opened by Super+A, the top bar, Nexus, or `nexus-shell-ipc call apps open [tab]`.
FloatingWindow {
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
    Process {
        id: change
        property string verb: ""
        stdout: SplitParser { onRead: line => { if (line.trim()) win.message = line.trim().slice(0, 140) } }
        stderr: SplitParser { onRead: line => { if (line.trim()) win.message = line.trim().slice(0, 140) } }
        onExited: code => {
            const name = win.nameOf(win.busy);
            win.message = code === 0 ? name + (change.verb === "install" ? " is installed. Find it in All apps." : " is removed.")
                                     : "Could not " + change.verb + " " + name + ". " + win.message;
            win.busy = "";
            listInstalled.running = true;
        }
    }
    function nameOf(id) { const r = results.concat(featured).find(x => x.id === id); return r ? r.name : id }
    function install(id) {
        if (busy) return;
        busy = id; message = "Installing " + nameOf(id) + "…"; change.verb = "install";
        // A per-person install needs Flathub in the person's own list of remotes.
        change.command = ["sh", "-c", "flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo && " +
                          "flatpak install --user --noninteractive -y flathub \"$1\"", "sh", id];
        change.running = true;
    }
    function uninstall(id) {
        if (busy) return;
        busy = id; message = "Removing " + nameOf(id) + "…"; change.verb = "remove";
        // Apps installs for the person; one installed for everyone is removed there (polkit asks).
        change.command = ["sh", "-c", "flatpak uninstall --user --noninteractive -y \"$1\" || flatpak uninstall --system --noninteractive -y \"$1\"", "sh", id];
        change.running = true;
    }
    onVisibleChanged: if (visible) listInstalled.running = true

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
        }
        function onFailed(id, message) { if (id === win.keepId) win.trialNote = "Keeping it did not work: " + message }
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
                : "Popular on Flathub. Search above for anything else. Apps install for you only, with no password."
            color: win.theme.inkSecondary; font.pixelSize: 13
        }
        model: query.text.trim() ? win.results : win.featured
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
                         enabled: win.busy === "" && win.flathub; onClicked: win.install(modelData.id) }
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

    // Ubuntu (apt).
    Column {
        visible: win.tab === "apt"
        x: 32; y: 110; width: parent.width - 64
        spacing: 10
        SettingRow {
            theme: win.theme; label: "apt, in an Ubuntu container"
            detail: "NexusOS is built on NixOS, which has no apt of its own. The apt command runs apt inside Ubuntu 24.04, " +
                    "which shares your home folder. Apps you install there are added to All apps. No password is needed."
            Button { theme: win.theme; primary: true; label: "Open a terminal"
                     onClicked: Quickshell.execDetached([Quickshell.env("NEXUS_TERMINAL") || "kitty"]) }
        }
        SettingRow {
            theme: win.theme; label: "Examples"
            detail: "apt install htop        apt search ffmpeg        apt remove htop        apt upgrade        apt shell (a terminal inside Ubuntu)"
        }
        SettingRow {
            theme: win.theme; label: "Software for the whole system"
            detail: "Drivers, services and software every account should have are added to NexusOS itself, in " +
                    "/etc/nixos/local.nix, and go through Guardian: you see the change and approve it, and it can be rolled back. " +
                    "To try a program first without installing it, run: nexus try <name>"
        }
    }
}
