import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Hyprland

// "Describe this" (Settings, Accessibility; Nexus's describe_screen card;
// Explain on a picture): a picture of the focused window, of a window or area
// the person points at (slurp: click a window or drag), or of the whole
// screen, taken with grim only when the person asks, into the private runtime
// folder. Core reads it, has the local vision model describe it, and deletes
// it (Vision.cs); it never goes to a cloud model. Answers are plain text.
// Each request carries a tag, so its answer goes back to whoever asked.
Item {
    id: describer
    property var bridge
    // From GET /vision/status: enabled (the computer has a vision model) and ready (it is downloaded).
    property bool enabled: false
    property bool ready: false
    property string model: ""
    property bool busy: false
    property string tag: ""
    property string question: ""
    property string title: ""
    property int requestId: -1
    property int statusId: -1
    readonly property string runtimeDir: (Quickshell.env("XDG_RUNTIME_DIR") || "/tmp") + "/nexus"
    readonly property string file: "describe.png"

    signal described(string tag, string text)
    signal failed(string tag, string message)

    function refresh() { if (bridge && bridge.online) statusId = bridge.api("GET", "/vision/status") }
    Component.onCompleted: refresh()
    Connections { target: describer.bridge; function onOnlineChanged() { describer.refresh() } }

    // target: "window" (the focused window), "area" (point at a window or drag over an area) or "screen".
    function describe(target, question, tagName) {
        tagName = tagName || "nexus";
        if (busy) { failed(tagName, "Still describing the last picture."); return }
        if (!bridge || !bridge.online) { failed(tagName, "Nexus Core is not running."); return }
        if (!enabled) { failed(tagName, "Describing pictures needs the local vision model, which is turned off on this computer."); return }
        tag = tagName; question = question || ""; title = "";
        busy = true;
        let geometry = "";
        if (target === "window") {
            const t = Hyprland.activeToplevel;
            const o = t && t.lastIpcObject;
            if (!o || !o.at) { busy = false; failed(tagName, "No window is focused. Point at one instead."); return }
            geometry = o.at[0] + "," + o.at[1] + " " + o.size[0] + "x" + o.size[1];
            title = t.title || "";
        }
        // A short pause, so Nexus or Settings has gone from the screen before the picture.
        capture.command = ["sh", "-c", script, "sh", target, geometry, boxes(), runtimeDir, file];
        pause.start();
    }
    // A picture the desktop already took into the runtime folder (Explain's area).
    function describeFile(name, question, title, tagName) {
        if (!bridge || !bridge.online || !enabled) { failed(tagName, "The local vision model is not available."); return }
        busy = true; tag = tagName;
        ask(name, question || "", title || "");
    }
    function ask(name, q, t) {
        requestId = bridge.api("POST", "/vision/describe", { image: name, question: q, title: t });
        if (requestId < 0) { busy = false; failed(tag, "Nexus Core is not running.") }
    }
    function boxes() {
        const ws = Hyprland.focusedWorkspace ? Hyprland.focusedWorkspace.id : -1;
        return Hyprland.toplevels.values.filter(t => t.workspace && t.workspace.id === ws && t.lastIpcObject && t.lastIpcObject.at)
            .map(t => { const o = t.lastIpcObject; return o.at[0] + "," + o.at[1] + " " + o.size[0] + "x" + o.size[1] }).join("\n");
    }
    Timer { id: pause; interval: 350; onTriggered: capture.running = true }
    // $1 target, $2 window geometry, $3 slurp's boxes, $4 folder, $5 file. Esc in slurp exits 3.
    readonly property string script:
        "mkdir -p \"$4\" && chmod 700 \"$4\" || exit 2\n" +
        "case \"$1\" in\n" +
        "  window) grim -g \"$2\" \"$4/$5\" ;;\n" +
        "  area) sel=$(printf '%s\\n' \"$3\" | slurp) || exit 3; grim -g \"$sel\" \"$4/$5\" ;;\n" +
        "  *) grim -s 0.75 \"$4/$5\" ;;\n" +
        "esac"
    Process {
        id: capture
        onExited: code => {
            if (code === 3) { describer.busy = false; describer.failed(describer.tag, "Nothing was chosen."); return }
            if (code !== 0) { describer.busy = false; describer.failed(describer.tag, "Could not take the picture."); return }
            describer.ask(describer.file, describer.question, describer.title);
        }
    }
    Connections {
        target: describer.bridge
        function onResult(id, type, data) {
            if (id === describer.statusId) {
                describer.statusId = -1;
                describer.enabled = !!data.enabled; describer.ready = !!data.ready; describer.model = data.model || "";
            } else if (id === describer.requestId) {
                describer.requestId = -1; describer.busy = false;
                describer.described(describer.tag, data.answer || "");
            }
        }
        function onFailed(id, message) {
            if (id === describer.statusId) { describer.statusId = -1; describer.enabled = false; describer.ready = false }
            else if (id === describer.requestId) {
                describer.requestId = -1; describer.busy = false;
                Quickshell.execDetached(["rm", "-f", describer.runtimeDir + "/" + describer.file]);
                describer.failed(describer.tag, message);
            }
        }
    }
}
