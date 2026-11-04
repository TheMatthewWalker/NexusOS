import QtQuick
import Quickshell
import Quickshell.Io
import "settings"

// Images: local image generation (design: "Image generation A · Gallery and
// inspector"). Describe a picture, choose a shape and a style, and Nexus makes
// it on this computer's GPU with the local image model (ImageStudio.cs).
// Results go to ~/Pictures/Nexus; nothing is uploaded. Two views of the same
// pictures, chosen in the options row and remembered per person
// (~/.config/nexusos/images.json): Gallery, a grid with an inspector, and
// Orbit (design: "Image generation B · Orbit"), the pictures around Nexus
// with the prompt at the centre and the chosen one beside it. Both views show
// win.shown and call the same functions (openImage, moreLike, remix, remove).
// Opened from Studio, Super+G, or `nexus-shell-ipc call images open`.
FloatingWindow {
    id: win
    property var theme
    property var bridge
    signal switchToCode()
    signal useAsWallpaper(string path)
    title: "Images"
    visible: false
    implicitWidth: 1240; implicitHeight: 800
    minimumSize: Qt.size(960, 640)
    color: "#05071A"

    property var status: null
    property var gallery: []            // newest first: { name, path, prompt, style, seed, width, height, steps, model, made }
    property var batch: []              // the latest results
    property var selected: null
    property bool working: false
    property int pendingCount: 0
    property string error: ""
    property string shape: "landscape"
    property string style: ""
    property int count: 4
    property int seed: -1
    property int genId: -1
    property int statusId: -1
    property int listId: -1
    property int deleteId: -1
    readonly property string hyprctl: Quickshell.env("NEXUS_HYPRCTL") || "hyprctl"
    readonly property string configDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos"
    // "gallery" or "orbit".
    readonly property string view: prefs.view === "orbit" ? "orbit" : "gallery"
    function setView(v) {
        if (v !== "gallery" && v !== "orbit") return;
        prefs.view = v;
        prefsFile.writeAdapter();
    }
    FileView {
        id: prefsFile
        path: win.configDir + "/images.json"
        adapter: JsonAdapter { id: prefs; property string view: "gallery" }
    }
    // What both views show: placeholders while making, else the latest batch, else the newest images.
    readonly property var shown: working ? Array.from({ length: pendingCount }, (_, i) => null) : (batch.length ? batch : gallery.slice(0, 4))
    // Ways to vary a picture: the chosen picture's prompt with a few words added.
    readonly property var variations: [
        { label: "Warmer light", words: "warm golden light" }, { label: "Wider view", words: "wide shot, more of the scene" },
        { label: "More detail", words: "highly detailed" }, { label: "Night version", words: "at night" }
    ]

    function open() {
        if (visible) Quickshell.execDetached([hyprctl, "dispatch", "focuswindow", "title:^(Images)$"]);
        visible = true;
        refresh();
        prompt.forceActiveFocus();
    }
    function refresh() {
        if (!bridge.online) return;
        statusId = bridge.send("images-status");
        listId = bridge.send("images-list");
    }
    // From Nexus (an action card, or `images make`): open and start on the description.
    function make(text) { open(); prompt.text = text; generate(text) }
    // The actions on a picture, the same in both views.
    function openImage(img) { if (img) Quickshell.execDetached(["xdg-open", img.path]) }
    function showFolder(img) { if (img) Quickshell.execDetached(["xdg-open", img.path.slice(0, img.path.lastIndexOf("/"))]) }
    function moreLike(img) { if (!img) return; prompt.text = img.prompt; style = img.style || ""; generate(img.prompt) }
    function again(img) { if (!img) return; prompt.text = img.prompt; count = 1; generate(img.prompt, img.seed) }
    function remix(img, words) {
        if (!img) return;
        const text = img.prompt.replace(/[\s,.]+$/, "") + ", " + words;
        prompt.text = text; style = img.style || ""; generate(text);
    }
    function remove(img) { if (img) deleteId = bridge.send("images-delete", { name: img.name }) }
    function generate(text, seedOverride) {
        if (working || !text.trim()) return;
        error = ""; working = true; pendingCount = count; batch = [];
        genId = bridge.send("images-generate", { prompt: text.trim(), shape: shape, count: count, style: style,
                                                 seed: seedOverride !== undefined ? seedOverride : seed });
    }
    Connections {
        target: win.bridge
        function onOnlineChanged() { if (win.visible) win.refresh() }
        function onResult(id, type, data) {
            if (id === win.statusId) win.status = data;
            else if (id === win.listId) { win.gallery = data.images || []; if (!win.selected && win.gallery.length) win.selected = win.gallery[0] }
            else if (id === win.genId) {
                win.working = false; win.batch = data.images || []; win.selected = win.batch[0] || null;
                win.listId = win.bridge.send("images-list");
            } else if (id === win.deleteId) {
                win.batch = win.batch.filter(i => i.name !== win.selected.name);
                win.selected = win.batch[0] || null;
                win.listId = win.bridge.send("images-list");
            }
        }
        function onFailed(id, message) {
            if (id === win.genId) { win.working = false; win.error = message }
            else if ([win.statusId, win.listId, win.deleteId].includes(id)) win.error = message;
        }
    }
    IpcHandler {
        target: "images"
        function open(): void { win.open() }
        function make(text: string): void { win.make(text) }
        // "gallery" or "orbit", as the View choice.
        function view(name: string): void { win.setView(name) }
        function state(): string {
            return JSON.stringify({ visible: win.visible, view: win.view, working: win.working, status: win.status, batch: win.batch.map(i => i.name),
                                    gallery: win.gallery.length, error: win.error })
        }
    }
    function fileUrl(p) { return "file://" + p.split("/").map(encodeURIComponent).join("/") }

    readonly property string modelLabel: !status ? "…" : status.model ? status.model
        : status.downloading ? "Downloading the image model…" : "No image model yet"

    Rectangle {
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0; color: "#0B0F24" }
            GradientStop { position: 1; color: "#020309" }
        }
    }

    // ---- prompt ----
    Column {
        id: top
        x: 20; y: 18; width: parent.width - 40; spacing: 10
        Rectangle {
            width: parent.width; height: 54; radius: 16; color: win.theme.glass
            border.color: prompt.activeFocus ? win.theme.focus : win.theme.glassBorder
            TextInput {
                id: prompt
                x: 18; width: parent.width - genButton.width - 40; anchors.verticalCenter: parent.verticalCenter
                color: win.theme.ink; font.pixelSize: 15; clip: true
                Text { visible: !prompt.text; text: "Describe the image, e.g. a lighthouse on a cliff at dusk"
                       color: win.theme.inkSecondary; font.pixelSize: 15 }
                onAccepted: win.generate(text)
                Accessible.role: Accessible.EditableText
                Accessible.name: "Describe the image"
            }
            Button {
                id: genButton
                anchors { right: parent.right; rightMargin: 8; verticalCenter: parent.verticalCenter }
                theme: win.theme; primary: true; enabled: !win.working && !!(win.status && win.status.model)
                label: win.working ? "Making…" : "Generate " + win.count
                onClicked: win.generate(prompt.text)
            }
        }
        Row {
            spacing: 8
            Choice { theme: win.theme; name: "Shape"; current: win.shape
                     options: [{ value: "landscape", label: "16 : 9" }, { value: "square", label: "Square" }, { value: "portrait", label: "9 : 16" }]
                     onChose: v => win.shape = v }
            Choice { theme: win.theme; name: "Style"; current: win.style
                     options: [{ value: "", label: "Any style" }, { value: "photo", label: "Photo" }, { value: "painterly", label: "Painterly" },
                               { value: "illustration", label: "Illustration" }, { value: "3d", label: "3D" }]
                     onChose: v => win.style = v }
            Choice { theme: win.theme; name: "How many"; current: win.count
                     options: [1, 2, 4].map(n => ({ value: n, label: n + "" })); onChose: v => win.count = v }
            Text { anchors.verticalCenter: parent.verticalCenter; leftPadding: 6; font.pixelSize: 12; color: win.theme.inkSecondary
                   text: "Model: " + win.modelLabel + " · runs on this computer" }
            Choice { theme: win.theme; name: "View"; current: win.view
                     options: [{ value: "gallery", label: "Gallery" }, { value: "orbit", label: "Orbit" }]
                     onChose: v => win.setView(v) }
            Text { anchors.verticalCenter: parent.verticalCenter; leftPadding: 16; font.pixelSize: 12; color: win.theme.focus
                   text: "Code"; TapHandler { onTapped: win.switchToCode() } }
        }
        Text {
            visible: win.error !== "" || (win.status && !win.status.model)
            width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; font.pixelSize: 13
            color: win.error ? win.theme.error : win.theme.inkSecondary
            text: win.error || (win.status && win.status.downloading
                ? "The image model (about 7 GB) is downloading in the background. Images can be made once it has finished."
                : "The image model downloads once this computer is online. Until then, nothing can be made here.")
        }
    }

    // ---- results ----
    Item {
        id: results
        visible: win.view === "gallery"
        x: 20; y: top.y + top.height + 14; width: parent.width - 40 - inspector.width - 16
        height: parent.height - y - history.height - 26
        readonly property var shown: win.shown
        readonly property int cols: shown.length > 1 ? 2 : 1
        readonly property int rows: Math.ceil(shown.length / cols)
        Text {
            visible: results.shown.length === 0
            anchors.centerIn: parent; color: win.theme.inkSecondary; font.pixelSize: 14
            text: "Your images appear here."
        }
        Grid {
            columns: results.cols; spacing: 12
            Repeater {
                model: results.shown
                delegate: Rectangle {
                    required property var modelData
                    required property int index
                    width: (results.width - (results.cols - 1) * 12) / results.cols
                    height: (results.height - (results.rows - 1) * 12) / results.rows
                    radius: 14; clip: true; color: "#0B1430"
                    border.color: modelData && win.selected && modelData.name === win.selected.name ? win.theme.focus : "transparent"
                    border.width: 2
                    Image {
                        anchors { fill: parent; margins: 2 }
                        visible: !!modelData
                        source: modelData ? win.fileUrl(modelData.path) : ""
                        fillMode: Image.PreserveAspectCrop; asynchronous: true; smooth: true
                    }
                    Column {
                        visible: !modelData
                        anchors.centerIn: parent; spacing: 8; width: parent.width * 0.6
                        Text { anchors.horizontalCenter: parent.horizontalCenter; color: win.theme.inkSecondary; font.pixelSize: 13
                               text: index === 0 ? "Making image 1 of " + win.pendingCount + "…" : "Waiting" }
                        Rectangle {
                            width: parent.width; height: 4; radius: 2; color: "#1B2647"
                            Rectangle {
                                width: parent.width * 0.3; height: 4; radius: 2; color: win.theme.focus
                                SequentialAnimation on x {
                                    running: index === 0 && win.working; loops: Animation.Infinite
                                    NumberAnimation { from: 0; to: parent.width * 0.7; duration: 1200 }
                                    NumberAnimation { from: parent.width * 0.7; to: 0; duration: 1200 }
                                }
                            }
                        }
                    }
                    TapHandler { enabled: !!modelData; onTapped: win.selected = modelData }
                    Accessible.role: Accessible.Button
                    Accessible.name: modelData ? "Image: " + modelData.prompt : "Being made"
                }
            }
        }
    }

    // ---- inspector ----
    Rectangle {
        id: inspector
        visible: win.view === "gallery"
        x: parent.width - width - 20; y: results.y; width: 280; height: results.height
        radius: 16; color: win.theme.glass; border.color: win.theme.glassBorder
        Column {
            visible: !!win.selected
            x: 18; y: 18; width: parent.width - 36; spacing: 10
            Text { width: parent.width; wrapMode: Text.Wrap; maximumLineCount: 4; elide: Text.ElideRight; textFormat: Text.PlainText
                   text: win.selected ? win.selected.prompt : ""; color: win.theme.ink; font.pixelSize: 14; font.weight: 600 }
            Grid {
                columns: 2; columnSpacing: 12; rowSpacing: 6
                Repeater {
                    model: win.selected ? ["Size", win.selected.width + " × " + win.selected.height, "Seed", String(win.selected.seed),
                                           "Steps", String(win.selected.steps), "Style", win.selected.style || "Any",
                                           "Model", win.selected.model] : []
                    delegate: Text {
                        required property var modelData
                        required property int index
                        text: modelData; font.pixelSize: 12
                        color: index % 2 === 0 ? win.theme.inkSecondary : win.theme.ink
                    }
                }
            }
            Column {
                width: parent.width; spacing: 8; topPadding: 6
                Button { theme: win.theme; width: parent.width; label: "Open"; onClicked: win.openImage(win.selected) }
                Button { theme: win.theme; width: parent.width; label: "More like this"; enabled: !win.working
                         onClicked: win.moreLike(win.selected) }
                Button { theme: win.theme; width: parent.width; label: "Same picture again (same seed)"; enabled: !win.working
                         onClicked: win.again(win.selected) }
                Button { theme: win.theme; width: parent.width; label: "Use as wallpaper"
                         onClicked: win.useAsWallpaper(win.selected.path) }
                Button { theme: win.theme; width: parent.width; label: "Show in folder"; onClicked: win.showFolder(win.selected) }
                Button { theme: win.theme; width: parent.width; danger: true; label: "Delete"; onClicked: win.remove(win.selected) }
                // A variation of the prompt, as in Orbit.
                Flow {
                    width: parent.width; spacing: 6
                    Repeater {
                        model: win.variations
                        delegate: Button { required property var modelData; theme: win.theme; label: modelData.label; enabled: !win.working
                                           implicitHeight: 28; onClicked: win.remix(win.selected, modelData.words) }
                    }
                }
            }
        }
        Text {
            anchors { left: parent.left; right: parent.right; bottom: parent.bottom; margins: 18 }
            wrapMode: Text.Wrap; color: win.theme.inkSecondary; font.pixelSize: 12; lineHeight: 1.4
            text: "Images stay in ~/Pictures/Nexus until you delete them. Nothing is uploaded."
        }
    }

    // ---- orbit: the same pictures around Nexus, the chosen one beside it ----
    Item {
        id: orbitView
        visible: win.view === "orbit"
        x: 20; y: top.y + top.height + 14; width: parent.width - 40
        height: parent.height - y - history.height - 26
        readonly property real areaWidth: width - chosen.width - 24
        readonly property real cx: areaWidth / 2
        readonly property real cy: height / 2
        readonly property real r: Math.max(80, Math.min(areaWidth, height) / 2 - 66)

        Rectangle {
            x: orbitView.cx - orbitView.r; y: orbitView.cy - orbitView.r; width: orbitView.r * 2; height: width; radius: width / 2
            color: "transparent"; border.color: Qt.rgba(0.55, 0.62, 1.0, 0.2)
        }
        Rectangle {
            x: orbitView.cx - orbitView.r * 0.92; y: orbitView.cy - 1; width: orbitView.r * 1.84; height: 2; opacity: 0.6
            gradient: Gradient { orientation: Gradient.Horizontal
                GradientStop { position: 0; color: "transparent" }
                GradientStop { position: 0.5; color: "#DCE4FF" }
                GradientStop { position: 1; color: "transparent" } }
        }
        // Nexus at the centre, with the prompt the pictures came from.
        Rectangle {
            id: orbitCore
            x: orbitView.cx - width / 2; y: orbitView.cy - height / 2; width: 140; height: 140; radius: 70
            gradient: Gradient {
                GradientStop { position: 0; color: win.working ? "#FFE3B8" : "#FFD9B0" }
                GradientStop { position: 0.4; color: win.working ? "#FFA65C" : "#D66840" }
                GradientStop { position: 0.75; color: "#5A3C9A" }
                GradientStop { position: 1; color: "#05071A" }
            }
            SequentialAnimation on opacity {
                running: win.working; loops: Animation.Infinite
                NumberAnimation { from: 1; to: 0.7; duration: 900 }
                NumberAnimation { from: 0.7; to: 1; duration: 900 }
            }
        }
        Column {
            x: orbitView.cx - width / 2; y: orbitCore.y + orbitCore.height + 10; width: Math.min(260, orbitView.r * 1.3); spacing: 4
            Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight
                   textFormat: Text.PlainText; color: win.theme.ink; font.pixelSize: 13
                   text: win.working ? prompt.text : win.shown.length && win.shown[0] ? win.shown[0].prompt : "" }
            Text { width: parent.width; horizontalAlignment: Text.AlignHCenter; color: win.theme.inkSecondary; font.pixelSize: 12
                   text: win.working ? "Making " + win.pendingCount + "…"
                       : win.shown.length === 0 ? "Your images appear here." : win.shown.length + (win.shown.length === 1 ? " picture" : " pictures") }
        }
        Repeater {
            model: win.shown
            delegate: Rectangle {
                id: moon
                required property var modelData
                required property int index
                readonly property bool on: !!modelData && !!win.selected && modelData.name === win.selected.name
                readonly property real angle: -Math.PI / 2 + index * Math.PI * 2 / Math.max(1, win.shown.length)
                width: on ? 124 : 96; height: width; radius: 22; clip: true; color: "#0B1430"
                x: orbitView.cx + orbitView.r * Math.cos(angle) - width / 2
                y: orbitView.cy + orbitView.r * Math.sin(angle) - height / 2
                border.color: on ? "#86A8FF" : "#2A3A66"; border.width: 2
                Image {
                    anchors { fill: parent; margins: 2 }
                    visible: !!moon.modelData
                    source: moon.modelData ? win.fileUrl(moon.modelData.path) : ""
                    sourceSize: Qt.size(256, 256); fillMode: Image.PreserveAspectCrop; asynchronous: true
                }
                Text { visible: !moon.modelData; anchors.centerIn: parent; color: win.theme.inkSecondary; font.pixelSize: 12
                       text: moon.index === 0 ? "Making…" : "Waiting" }
                TapHandler { enabled: !!moon.modelData; onTapped: win.selected = moon.modelData }
                Accessible.role: Accessible.Button
                Accessible.name: moon.modelData ? "Image " + (moon.index + 1) + ": " + moon.modelData.prompt : "Being made"
                Accessible.selected: on
            }
        }

        // The chosen picture, with what can be done with it.
        Rectangle {
            id: chosen
            anchors.right: parent.right; width: Math.min(460, parent.width * 0.42); height: chosenColumn.implicitHeight + 36
            anchors.verticalCenter: parent.verticalCenter
            radius: 20; color: win.theme.glass; border.color: win.selected ? Qt.rgba(0.53, 0.66, 1, 0.55) : win.theme.glassBorder
            Column {
                id: chosenColumn
                x: 18; y: 18; width: parent.width - 36; spacing: 12
                Rectangle {
                    width: parent.width; height: Math.min(260, orbitView.height - 230); radius: 14; clip: true; color: "#0B1430"
                    Image { anchors.fill: parent; visible: !!win.selected; source: win.selected ? win.fileUrl(win.selected.path) : ""
                            fillMode: Image.PreserveAspectFit; asynchronous: true; smooth: true }
                    Text { visible: !win.selected; anchors.centerIn: parent; color: win.theme.inkSecondary; font.pixelSize: 13
                           text: "Choose a picture on the orbit." }
                }
                Text { width: parent.width; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight; textFormat: Text.PlainText
                       text: win.selected ? win.selected.prompt : ""; color: win.theme.ink; font.pixelSize: 15; font.weight: 600 }
                Text { visible: !!win.selected; color: win.theme.inkSecondary; font.pixelSize: 12
                       text: win.selected ? win.selected.width + " × " + win.selected.height + " · seed " + win.selected.seed + " · saved in Pictures/Nexus" : "" }
                // A variation of the prompt: the chosen picture's words plus a few more.
                Flow {
                    visible: !!win.selected
                    width: parent.width; spacing: 8
                    Repeater {
                        model: win.variations
                        delegate: Rectangle {
                            required property var modelData
                            width: chip.implicitWidth + 24; height: 30; radius: 15
                            color: ch.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.22) : "#0E1630"; border.color: "#2A3A66"
                            opacity: win.working ? 0.5 : 1
                            Text { id: chip; anchors.centerIn: parent; text: modelData.label; color: win.theme.ink; font.pixelSize: 12 }
                            HoverHandler { id: ch; cursorShape: Qt.PointingHandCursor }
                            TapHandler { enabled: !win.working; onTapped: win.remix(win.selected, modelData.words) }
                            Accessible.role: Accessible.Button
                            Accessible.name: "Variation: " + modelData.label
                        }
                    }
                }
                Row {
                    visible: !!win.selected
                    spacing: 8
                    Button { theme: win.theme; primary: true; label: "Open"; onClicked: win.openImage(win.selected) }
                    Button { theme: win.theme; label: "More like this"; enabled: !win.working; onClicked: win.moreLike(win.selected) }
                    Button { theme: win.theme; label: "Same seed"; enabled: !win.working; onClicked: win.again(win.selected) }
                }
                Row {
                    visible: !!win.selected
                    spacing: 8
                    Button { theme: win.theme; label: "Show in folder"; onClicked: win.showFolder(win.selected) }
                    Button { theme: win.theme; label: "Use as wallpaper"; onClicked: win.useAsWallpaper(win.selected.path) }
                    Button { theme: win.theme; danger: true; label: "Delete"; onClicked: win.remove(win.selected) }
                }
            }
        }
    }

    // ---- earlier images ----
    Row {
        id: history
        x: 20; y: parent.height - height - 16; height: 64; spacing: 8
        Text { text: "Earlier"; color: win.theme.inkSecondary; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter; rightPadding: 6 }
        Repeater {
            model: win.gallery.slice(0, Math.max(0, Math.floor((win.width - 120) / 72)))
            delegate: Rectangle {
                required property var modelData
                width: 64; height: 64; radius: 10; clip: true; color: "#0B1430"
                border.color: win.selected && modelData.name === win.selected.name ? win.theme.focus : "transparent"; border.width: 2
                Image { anchors { fill: parent; margins: 2 } source: win.fileUrl(modelData.path); fillMode: Image.PreserveAspectCrop
                        asynchronous: true; sourceSize: Qt.size(128, 128) }
                TapHandler { onTapped: { win.selected = modelData; win.batch = [modelData] } }
                Accessible.role: Accessible.Button
                Accessible.name: "Earlier image: " + modelData.prompt
            }
        }
    }
}
