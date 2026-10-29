import QtQuick
import Quickshell
import Quickshell.Io

// The shell's only channel to Nexus Core: one long-lived `nexus bridge`
// process, so the shell never holds the API token or opens the socket itself.
// Requests and events are JSON lines tagged with an id. If the bridge exits or
// Core stops answering, `online` goes false and the bridge is restarted with
// backoff; the rest of the desktop keeps working without AI.
Item {
    id: bridge
    property string cli: Quickshell.env("NEXUS_CLI") || "nexus"
    property bool online: false
    property string offlineReason: "Connecting to Nexus Core…"
    property int nextId: 1
    property int restartDelay: 1000
    property var pending: ({})          // id -> op, for routing replies

    signal delta(int id, string text)
    signal done(int id, string provider)
    signal failed(int id, string message)
    signal suggestions(int id, var apps, string source)
    signal unloaded(int id, var vramFreedBytes)     // bytes measured by Core, or null when unknown
    signal result(int id, string type, var data)     // providers, status, cleared, code-session, code-result
    signal codeEvent(int id, var ev)                 // the coding agent's step, changes, message and done events

    function send(op, extra) {
        if (!proc.running) return -1;
        const id = nextId++;
        pending[id] = op;
        proc.write(JSON.stringify(Object.assign({ id: id, op: op }, extra || {})) + "\n");
        return id;
    }
    function chat(prompt) { return send("chat", { prompt: prompt }) }
    function suggest(prompt, useModel) { return send("suggest", { prompt: prompt, model: !!useModel }) }
    function unload() { return send("unload") }

    function handle(line) {
        let ev;
        try { ev = JSON.parse(line) } catch (e) { return }
        // "ready" means only that the bridge started; Core is online once it answers.
        if (ev.type === "ready") { restartDelay = 1000; health.restart(); send("health"); return }
        const id = typeof ev.id === "number" ? ev.id : -1;
        if (ev.type === "delta") { delta(id, ev.text); return }
        if (pending[id] === "code-send" && ev.type !== "error") {
            if (ev.type === "done") delete pending[id];
            codeEvent(id, ev);
            return;
        }
        delete pending[id];
        if (ev.type === "done") done(id, ev.provider);
        else if (ev.type === "suggestions") suggestions(id, ev.data.apps, ev.data.source);
        else if (ev.type === "unloaded") unloaded(id, ev.data.vramFreedBytes);
        else if (ev.type === "health") { online = true; offlineReason = "" }
        else if (["providers", "status", "cleared", "code-session", "code-result", "images-status", "images", "images-deleted"].includes(ev.type)) result(id, ev.type, ev.data || {});
        else if (ev.type === "error") {
            if (/not reachable|did not answer/.test(ev.message)) { online = false; offlineReason = ev.message }
            failed(id, ev.message);
        }
    }

    Process {
        id: proc
        command: [bridge.cli, "bridge"]
        running: true
        stdinEnabled: true
        stdout: SplitParser { onRead: data => bridge.handle(data) }
        stderr: StdioCollector { id: errors }
        onExited: (code) => {
            bridge.online = false;
            bridge.offlineReason = (errors.text || "").trim().split("\n")[0] || "Nexus Core is not running.";
            for (const id in bridge.pending) bridge.failed(Number(id), bridge.offlineReason);
            bridge.pending = {};
            restart.interval = bridge.restartDelay;
            bridge.restartDelay = Math.min(30000, bridge.restartDelay * 2);
            restart.start();
        }
    }
    Timer { id: restart; onTriggered: proc.running = true }
    // While online, a cheap health probe notices Core going away between requests.
    Timer { id: health; interval: 15000; repeat: true; running: proc.running; onTriggered: bridge.send("health") }
}
