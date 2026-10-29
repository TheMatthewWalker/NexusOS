import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Hyprland
import "settings"

// Apps: every installed app (and a search box, so it doubles as the app
// launcher), getting new ones from Flathub, and apt in the Ubuntu container.
// Flathub apps install for this person only (flatpak --user): no password
// and no system change. Software for the whole system goes through Guardian
// (/etc/nixos/local.nix); this window says so rather than doing it.
// Opened by Super+A, the top bar, Nexus, or `nexus-shell-ipc call apps open [tab]`.
FloatingWindow {
    id: win
    property var theme
    title: "Apps"
    visible: false
    implicitWidth: 1040; implicitHeight: 720
    minimumSize: Qt.size(760, 560)
    color: "#05071A"

    property string tab: "all"          // all | get | apt
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    function open(t) {
        if (["all", "get", "apt"].includes(t)) tab = t;
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
                                    installed: win.installedIds, busy: win.busy, message: win.message })
        }
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
        change.command = ["flatpak", "uninstall", "--noninteractive", "-y", id];
        change.running = true;
    }
    onVisibleChanged: if (visible) listInstalled.running = true

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
                options: [{ value: "all", label: "All apps" }, { value: "get", label: "Get apps" }, { value: "apt", label: "Ubuntu (apt)" }]
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
                       text: win.tab === "get" ? "Search Flathub, e.g. spotify" : "Search your apps" }
                onTextEdited: if (win.tab === "get") searchDelay.restart()
                // Enter opens the first app, like a launcher.
                onAccepted: {
                    if (win.tab === "all") {
                        const first = win.allApps.find(e => win.matches(e, text.trim()));
                        if (first) { first.execute(); win.visible = false; text = "" }
                    } else win.searchFlathub();
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
