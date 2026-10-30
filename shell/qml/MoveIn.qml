import QtQuick
import Quickshell
import Quickshell.Io
import "settings"

// Move in from Windows or macOS (feature 5, design "Feat-MoveIn"): pick the
// old drive (lsblk lists its partitions) or a backup folder; a drive is
// mounted read-only through udisks (udisksctl mount -o ro), so nothing on it
// can change. Nexus looks in the usual places (Users/<name>/Documents,
// Pictures, Music, Videos or Movies, Desktop, Downloads), shows their sizes,
// and copies the ticked folders into the home folder with rsync: files are
// copied, never moved, and nothing already in the home folder is overwritten
// (--ignore-existing). Everything stays on this computer. Browser bookmarks
// and app equivalents are coming later, and the window says so.
// The drive is not trusted: lib/movein.sh does the looking and copying, with
// NUL-separated output, checked names, no symlinks or special files, and
// destinations only from a fixed list under the home folder (see the script).
// Opened from Welcome, Settings, About, or `nexus-shell-ipc call movein open`.
FloatingWindow {
    id: win
    property var theme
    title: "Bring my stuff over"
    visible: false
    implicitWidth: 1160; implicitHeight: 740
    minimumSize: Qt.size(900, 600)
    color: "#05071A"

    readonly property string home: Quickshell.env("HOME") || ""
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    readonly property string helper: Quickshell.env("NEXUS_MOVEIN") || Quickshell.shellPath("lib/movein.sh")
    // Partitions that belong to this system are never offered.
    readonly property var systemMounts: ["/", "/boot", "/boot/efi", "/efi", "/nix", "/nix/store", "/home", "/var", "[SWAP]"]
    readonly property var folderNames: ["Documents", "Desktop", "Downloads", "Pictures", "Music", "Videos", "Movies"]
    // A user-folder name from the drive: one plain component (as movein.sh checks it again).
    function validUser(name) { return name === "" || (name.length <= 255 && !/^\.|\/|[\x00-\x1f\x7f]/.test(name)) }
    property var drives: []
    property string source: ""             // device path, or a folder path
    property string sourceKind: ""         // drive | folder
    property string root: ""               // where the old files are (mount point or folder)
    property bool weMounted: false
    property string os: ""
    property var found: []                 // { user, folder, bytes, on }
    property string status: ""
    property string phase: "pick"          // pick | mounting | scanning | ready | copying | done
    property real freeBytes: -1
    property var copyQueue: []
    property var copying: null
    property real copiedBytes: 0
    property real currentPct: 0

    function open() {
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Bring my stuff over)$"]);
        visible = true;
        refresh();
    }
    function refresh() { lsblk.running = true; df.running = true }
    function gb(bytes) {
        if (bytes < 0) return "…";
        if (bytes >= 1e9) return (bytes / 1e9).toFixed(bytes >= 1e10 ? 0 : 1) + " GB";
        if (bytes >= 1e6) return Math.round(bytes / 1e6) + " MB";
        return Math.max(0, Math.round(bytes / 1e3)) + " KB";
    }
    // lsblk's tree, flattened to the partitions (or whole disks) that hold a file system.
    function parseDrives(text) {
        let tree;
        try { tree = JSON.parse(text).blockdevices || [] } catch (e) { return [] }
        const out = [];
        const walk = (dev, parent) => {
            const mounts = (dev.mountpoints || []).filter(m => m);
            const usable = dev.fstype && dev.fstype !== "swap" && !mounts.some(m => systemMounts.includes(m) || m.startsWith("/nix"));
            if ((dev.type === "part" || (dev.type === "disk" && !dev.children)) && usable) {
                const fs = dev.fstype.toLowerCase();
                out.push({
                    path: dev.path, size: Number(dev.size) || 0, fstype: fs, mounts: mounts,
                    name: dev.label || dev.partlabel || (parent && parent.model ? parent.model.trim() : "") || dev.name,
                    removable: !!(dev.rm || dev.hotplug || (parent && (parent.rm || parent.hotplug))),
                    guess: fs === "ntfs" ? "Windows" : fs === "apfs" || fs === "hfsplus" ? "Mac" : fs === "crypto_luks" ? "Locked" : ""
                });
            }
            for (const c of dev.children || []) walk(c, dev);
        };
        for (const d of tree) walk(d, null);
        return out;
    }
    function choose(path) {
        if (busy()) return false;
        found = []; os = ""; root = ""; status = "";
        const d = drives.find(x => x.path === path);
        if (d) {
            source = path; sourceKind = "drive";
            if (d.fstype === "crypto_luks") { status = "This drive is locked (encrypted). Unlock it in Disks first, then choose it again."; phase = "pick"; return false }
            if (d.mounts.length) { root = d.mounts[0]; weMounted = false; scan(); return true }
            phase = "mounting"; status = "Opening the drive read-only…";
            mount.command = ["udisksctl", "mount", "--no-user-interaction", "-b", path, "-o", "ro"];
            mount.running = true;
            return true;
        }
        if (path.startsWith("/")) { source = path; sourceKind = "folder"; root = path; weMounted = false; scan(); return true }
        return false;
    }
    function scan() {
        phase = "scanning"; status = "Looking in the usual places and measuring folders…";
        scanner.command = ["sh", helper, "scan", root];
        scanner.running = true;
    }
    // Where a folder goes in the home folder: Movies becomes Videos; with several people on the old drive, each gets a subfolder.
    function destination(item) {
        const name = item.folder === "Movies" ? "Videos" : item.folder;
        return home + "/" + name + (perUser() && item.user ? "/" + item.user : "");
    }
    function perUser() { return new Set(found.map(f => f.user)).size > 1 }
    function toggle(user, folder) {
        found = found.map(f => f.user === user && f.folder === folder ? Object.assign({}, f, { on: !f.on }) : f);
    }
    readonly property real selectedBytes: found.filter(f => f.on).reduce((a, f) => a + f.bytes, 0)
    function busy() { return ["mounting", "scanning", "copying"].includes(phase) }
    function copy() {
        const items = found.filter(f => f.on);
        if (!items.length || busy()) return false;
        if (freeBytes >= 0 && selectedBytes > freeBytes) { status = "Not enough free space: " + gb(selectedBytes) + " to copy, " + gb(freeBytes) + " free."; return false }
        phase = "copying"; copiedBytes = 0; copyQueue = items; status = "";
        next();
        return true;
    }
    function next() {
        if (!copyQueue.length) {
            copying = null; phase = "done"; df.running = true;
            status = "Copied. Your files are in your home folder (Documents, Pictures and so on); the old drive was not changed.";
            return;
        }
        copying = copyQueue[0]; copyQueue = copyQueue.slice(1); currentPct = 0;
        // The helper works out both paths again from the root, the user and the folder name.
        rsync.command = ["sh", helper, "copy", root, copying.user, copying.folder, perUser() && copying.user ? "1" : "0"];
        rsync.running = true;
    }
    function cancel() { if (rsync.running) { copyQueue = []; rsync.running = false } }
    function unmount() {
        if (sourceKind === "drive" && weMounted && !busy()) {
            Quickshell.execDetached(["udisksctl", "unmount", "--no-user-interaction", "-b", source]);
            weMounted = false; status = "The old drive is closed. You can unplug it.";
        }
    }

    Process {
        id: lsblk
        command: ["lsblk", "-J", "-b", "-o", "NAME,PATH,SIZE,FSTYPE,LABEL,PARTLABEL,MOUNTPOINTS,TYPE,RM,HOTPLUG,MODEL"]
        stdout: StdioCollector { onStreamFinished: win.drives = win.parseDrives(text) }
    }
    Process {
        id: df
        command: ["df", "-P", "-B1", win.home]
        stdout: StdioCollector {
            onStreamFinished: { const f = (text.split("\n")[1] || "").trim().split(/\s+/); win.freeBytes = f.length > 3 ? Number(f[3]) : -1 }
        }
    }
    Process {
        id: mount
        stdout: StdioCollector { id: mountOut }
        stderr: StdioCollector { id: mountErr }
        onExited: code => {
            const m = /Mounted \S+ at (.+?)\.?\s*$/.exec(mountOut.text.trim());
            if (code === 0 && m) { win.root = m[1]; win.weMounted = true; win.refresh(); win.scan(); return }
            win.phase = "pick";
            const err = (mountErr.text || mountOut.text).trim().split("\n").pop() || "udisksctl stopped (" + code + ")";
            win.status = /NotAuthorized|not authorized/i.test(err)
                ? "This drive needs an administrator to open it (it is inside this computer). Ask the computer's owner, or copy from a USB drive or a backup folder instead."
                : /apfs/i.test(err) || win.drives.some(d => d.path === win.source && d.fstype === "apfs")
                ? "This is a Mac drive (APFS), which NexusOS cannot open yet. Copy the files to a USB drive from the Mac, or use a backup folder."
                : "The drive could not be opened: " + err;
        }
    }
    Process {
        id: scanner
        stdout: StdioCollector {
            onStreamFinished: {
                const items = [];
                for (const rec of text.split("\0")) {
                    const f = rec.split("\t");
                    if (f[0] === "os" && f.length === 2) { win.os = f[1] === "Mac" ? "Mac" : "Windows"; continue }
                    if (f[0] !== "item" || f.length !== 4 || !win.validUser(f[1]) || !win.folderNames.includes(f[2])) continue;
                    items.push({ user: f[1], folder: f[2], bytes: Number(f[3]) || 0, on: f[2] !== "Downloads" });
                }
                win.found = items;
                win.phase = "ready";
                win.status = items.length ? "" : "No Documents, Pictures, Music, Videos or Desktop folders were found there.";
            }
        }
    }
    Process {
        id: rsync
        stdout: SplitParser {
            splitMarker: "\r"
            onRead: line => { const m = /\s(\d+)%\s/.exec(line); if (m) win.currentPct = Number(m[1]) / 100 }
        }
        stderr: StdioCollector { id: rsyncErr }
        onExited: code => {
            if (code !== 0 && win.copying) {
                win.copyQueue = []; win.copying = null; win.phase = "ready";
                win.status = code === 143 || code === 20 ? "Copying stopped. Files copied so far are kept."
                           : "Copying stopped: " + (rsyncErr.text.trim().split("\n").pop() || "rsync stopped (" + code + ")");
                return;
            }
            win.copiedBytes += win.copying ? win.copying.bytes : 0;
            win.next();
        }
    }

    IpcHandler {
        target: "movein"
        function open(): void { win.open() }
        function close(): void { win.visible = false }
        function refresh(): void { win.refresh() }
        // A drive's device path (from state) or a folder holding the old files.
        function choose(path: string): bool { win.open(); return win.choose(path) }
        function toggle(user: string, folder: string): void { win.toggle(user, folder) }
        function copy(): bool { return win.copy() }
        function cancel(): void { win.cancel() }
        function unmount(): void { win.unmount() }
        function state(): string {
            return JSON.stringify({ visible: win.visible, phase: win.phase, status: win.status, source: win.source, root: win.root, os: win.os,
                                    drives: win.drives.map(d => ({ path: d.path, name: d.name, fstype: d.fstype, size: d.size, mounted: d.mounts.length > 0 })),
                                    found: win.found.map(f => ({ user: f.user, folder: f.folder, bytes: f.bytes, on: f.on, to: win.destination(f) })),
                                    selectedBytes: win.selectedBytes, freeBytes: win.freeBytes })
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
        id: header
        x: 30; y: 26; width: parent.width - 60; spacing: 6
        Text { text: "Bring my stuff over"; color: win.theme.ink; font.pixelSize: 24; font.weight: 600 }
        Text {
            width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 13
            text: "Choose the old drive or a backup folder. Nexus opens it read-only and looks in the usual places. " +
                  "Tick what to bring. Everything is copied on this computer; nothing is uploaded."
        }
    }

    // FROM
    Column {
        id: from
        x: 30; y: header.y + header.height + 20; width: 300; spacing: 8
        Text { text: "FROM"; color: win.theme.inkSecondary; font.pixelSize: 12; font.letterSpacing: 0.5 }
        Repeater {
            model: win.drives
            delegate: Rectangle {
                required property var modelData
                readonly property bool on: win.source === modelData.path
                width: from.width; height: driveCol.implicitHeight + 24; radius: 14; color: "#0E1630"
                border.color: on || activeFocus ? "#86A8FF" : driveHover.hovered ? "#3A4C80" : "#1F2C55"
                activeFocusOnTab: true
                Keys.onReturnPressed: win.choose(modelData.path)
                Keys.onSpacePressed: win.choose(modelData.path)
                Rectangle { x: 14; y: 15; width: 16; height: 16; radius: 8; color: "transparent"; border.color: on ? "#5F4FE0" : win.theme.glassBorder; border.width: 2
                            Rectangle { anchors.centerIn: parent; width: 8; height: 8; radius: 4; color: "#5F4FE0"; visible: parent.parent.on } }
                Column {
                    id: driveCol
                    x: 42; y: 12; width: parent.width - 56; spacing: 3
                    Text { width: parent.width; elide: Text.ElideRight; textFormat: Text.PlainText
                           text: modelData.name; color: win.theme.ink; font.pixelSize: 14; font.weight: 500 }
                    Text { width: parent.width; elide: Text.ElideRight; color: win.theme.inkSecondary; font.pixelSize: 12
                           text: [win.gb(modelData.size), modelData.guess, modelData.fstype.toUpperCase(), modelData.removable ? "removable" : ""].filter(x => x).join(" · ") }
                    Text { visible: on && win.root !== ""; color: "#7FE0B0"; font.pixelSize: 12
                           text: win.weMounted || modelData.mounts.length ? "Opened read-only" : "" }
                }
                HoverHandler { id: driveHover; cursorShape: Qt.PointingHandCursor }
                TapHandler { onTapped: win.choose(modelData.path) }
                Accessible.role: Accessible.RadioButton
                Accessible.checked: on
                Accessible.name: modelData.name + ", " + win.gb(modelData.size)
            }
        }
        Text { visible: win.drives.length === 0; width: from.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 13
               text: "No other drives found. Plug in the old drive (or a USB adapter for it), then press Look again." }
        // A backup folder.
        Rectangle {
            width: from.width; height: folderCol.implicitHeight + 24; radius: 14; color: "#0E1630"
            border.color: win.sourceKind === "folder" ? "#86A8FF" : "#1F2C55"
            Column {
                id: folderCol
                x: 14; y: 12; width: parent.width - 28; spacing: 8
                Text { text: "A backup folder"; color: win.theme.ink; font.pixelSize: 14; font.weight: 500 }
                Rectangle {
                    width: parent.width; height: 34; radius: 9; color: win.theme.chip
                    border.color: folderField.activeFocus ? win.theme.focus : win.theme.glassBorder
                    TextInput {
                        id: folderField
                        x: 10; width: parent.width - 20; anchors.verticalCenter: parent.verticalCenter
                        color: win.theme.ink; font.pixelSize: 12; clip: true
                        Text { visible: !folderField.text; text: "e.g. /run/media/you/Backup"; color: win.theme.inkSecondary; font.pixelSize: 12 }
                        onAccepted: win.choose(text.trim().startsWith("~") ? win.home + text.trim().slice(1) : text.trim())
                        Accessible.role: Accessible.EditableText
                        Accessible.name: "Backup folder"
                    }
                }
            }
        }
        Rectangle {
            width: from.width; height: 62; radius: 14; color: "#0E1630"; border.color: "#1F2C55"; opacity: 0.7
            Column {
                x: 14; anchors.verticalCenter: parent.verticalCenter; spacing: 3
                Text { text: "Another computer"; color: win.theme.ink; font.pixelSize: 14; font.weight: 500 }
                Text { text: "Over the network: coming later"; color: win.theme.inkSecondary; font.pixelSize: 12 }
            }
        }
        Button { theme: win.theme; label: "Look again"; enabled: !win.busy(); onClicked: win.refresh() }
    }

    // What was found.
    Flickable {
        id: foundView
        x: from.x + from.width + 24; y: from.y; width: parent.width - x - 30; height: parent.height - y - footer.height - 16
        contentHeight: foundCol.implicitHeight; clip: true
        boundsBehavior: Flickable.StopAtBounds
        Column {
            id: foundCol
            width: foundView.width; spacing: 14
            Text {
                visible: win.root !== "" || win.status !== ""
                width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; font.pixelSize: 13
                color: win.phase === "pick" && win.status ? win.theme.error : win.theme.inkSecondary
                text: win.status || (win.os ? "Found a " + win.os + " drive at " + win.root : "Looking in " + win.root)
            }
            Text {
                visible: win.root === "" && win.status === ""
                width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 14; topPadding: 20
                text: "Choose where your old files are, on the left."
            }
            Grid {
                visible: win.found.length > 0
                width: parent.width; columns: 2; spacing: 12
                Repeater {
                    model: [
                        { name: "Documents", dot: "#86A8FF", folders: ["Documents", "Desktop", "Downloads"], note: "Documents, Desktop and Downloads" },
                        { name: "Photos, music and video", dot: "#FFA65C", folders: ["Pictures", "Music", "Videos", "Movies"], note: "Pictures, Music and Videos" }
                    ]
                    delegate: Rectangle {
                        id: group
                        required property var modelData
                        readonly property var items: win.found.filter(f => modelData.folders.includes(f.folder))
                        width: (foundCol.width - 12) / 2; height: groupCol.implicitHeight + 28; radius: 14; color: "#0E1630"; border.color: "#1F2C55"
                        Column {
                            id: groupCol
                            x: 16; y: 14; width: parent.width - 32; spacing: 8
                            Item {
                                width: parent.width; height: 22
                                Rectangle { anchors.verticalCenter: parent.verticalCenter; width: 10; height: 10; radius: 5; color: group.modelData.dot }
                                Text { x: 18; anchors.verticalCenter: parent.verticalCenter; text: group.modelData.name; color: win.theme.ink; font.pixelSize: 15; font.weight: 500 }
                                Text { anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                                       text: win.gb(group.items.filter(f => f.on).reduce((a, f) => a + f.bytes, 0)); color: win.theme.ink; font.pixelSize: 13 }
                            }
                            Text { visible: group.items.length === 0; text: "Nothing found"; color: win.theme.muted; font.pixelSize: 12 }
                            Repeater {
                                model: group.items
                                delegate: Item {
                                    required property var modelData
                                    width: groupCol.width; height: 30
                                    Rectangle {
                                        id: box
                                        anchors.verticalCenter: parent.verticalCenter; width: 18; height: 18; radius: 5
                                        color: modelData.on ? "#5F4FE0" : "transparent"; border.color: modelData.on ? "#5F4FE0" : win.theme.glassBorder
                                        border.width: 1.5
                                        Text { anchors.centerIn: parent; visible: modelData.on; text: "✓"; color: "#FFFFFF"; font.pixelSize: 12 }
                                    }
                                    Text { x: 28; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 120; elide: Text.ElideRight; textFormat: Text.PlainText
                                           text: modelData.folder + (modelData.user ? " · " + modelData.user : ""); color: win.theme.ink; font.pixelSize: 13 }
                                    Text { anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                                           text: win.gb(modelData.bytes); color: win.theme.inkSecondary; font.pixelSize: 12 }
                                    HoverHandler { cursorShape: Qt.PointingHandCursor }
                                    TapHandler { enabled: !win.busy(); onTapped: win.toggle(modelData.user, modelData.folder) }
                                    activeFocusOnTab: true
                                    Keys.onSpacePressed: win.toggle(modelData.user, modelData.folder)
                                    Accessible.role: Accessible.CheckBox
                                    Accessible.checkable: true
                                    Accessible.checked: modelData.on
                                    Accessible.name: modelData.folder + ", " + win.gb(modelData.bytes)
                                }
                            }
                        }
                    }
                }
            }
            // Coming later, said plainly.
            Rectangle {
                visible: win.found.length > 0
                width: parent.width; height: laterCol.implicitHeight + 28; radius: 14; color: "#0E1630"; border.color: "#1F2C55"
                Column {
                    id: laterCol
                    x: 16; y: 14; width: parent.width - 32; spacing: 6
                    Text { text: "Browser bookmarks and passwords · Apps"; color: win.theme.ink; font.pixelSize: 15; font.weight: 500 }
                    Text {
                        width: parent.width; wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 12
                        text: "Coming later. For now, export bookmarks from your old browser to a file and import them in Firefox; " +
                              "for apps, Apps lists what NexusOS has (for example LibreOffice for Microsoft Office)."
                    }
                }
            }
            // Copying progress.
            Column {
                visible: win.phase === "copying"
                width: parent.width; spacing: 6
                Text { text: win.copying ? "Copying " + win.copying.folder + " to " + win.destination(win.copying).replace(win.home, "~") + "…" : ""
                       color: win.theme.ink; font.pixelSize: 13; textFormat: Text.PlainText }
                Rectangle {
                    width: parent.width; height: 8; radius: 4; color: win.theme.chip
                    Rectangle {
                        height: parent.height; radius: 4; color: "#7FE0B0"
                        width: parent.width * Math.min(1, win.selectedBytes > 0
                            ? (win.copiedBytes + win.currentPct * (win.copying ? win.copying.bytes : 0)) / win.selectedBytes : win.currentPct)
                    }
                }
            }
        }
    }

    Rectangle {
        id: footer
        anchors { left: parent.left; right: parent.right; bottom: parent.bottom; leftMargin: 30; rightMargin: 30; bottomMargin: 20 }
        height: 56; color: "transparent"
        Rectangle { width: parent.width; height: 1; color: "#1B2647" }
        Text {
            anchors.verticalCenter: parent.verticalCenter; width: parent.width - buttons.width - 20; wrapMode: Text.Wrap
            color: win.theme.inkSecondary; font.pixelSize: 12
            text: "Files are copied, never moved or changed on the old drive, and nothing already in your home folder is replaced." +
                  (win.freeBytes >= 0 ? " " + win.gb(win.freeBytes) + " free here." : "")
        }
        Row {
            id: buttons
            anchors { right: parent.right; verticalCenter: parent.verticalCenter }
            spacing: 10
            Button { theme: win.theme; visible: win.phase === "copying"; label: "Stop"; onClicked: win.cancel() }
            Button { theme: win.theme; visible: win.weMounted && !win.busy(); label: "Close the old drive"; onClicked: win.unmount() }
            Button { theme: win.theme; visible: win.phase === "done"; label: "Open home folder"; onClicked: Quickshell.execDetached(["xdg-open", win.home]) }
            Button {
                theme: win.theme; primary: true
                enabled: win.phase === "ready" && win.selectedBytes >= 0 && win.found.some(f => f.on)
                label: win.phase === "copying" ? "Copying…" : "Copy files (" + win.gb(win.selectedBytes) + ")"
                onClicked: win.copy()
            }
        }
    }
}
