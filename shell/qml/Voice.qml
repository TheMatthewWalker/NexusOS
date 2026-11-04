import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Wayland

// Voice control (Settings, Accessibility): push-to-talk and spoken replies,
// both on this computer (Voice.cs in Core).
//  - The microphone opens only while Super+V is held, or after the
//    microphone button in Nexus is pressed (hold it, or click once to start
//    and again to send). A red "Listening" bar shows at the top of the screen
//    the whole time; nothing listens otherwise. pw-record writes the
//    recording into the private runtime folder; Core turns it into text with
//    whisper.cpp and deletes it, and the text goes to Nexus as a typed
//    question would (heard). A recording stops by itself after 30 seconds.
//  - Spoken replies, when turned on, are read by Piper; pw-play plays them,
//    and pressing Super+V again (or Esc in Nexus) stops them.
// The person's choices are kept in ~/.config/nexusos/voice.json.
Item {
    id: voice
    property var theme
    property var bridge
    property alias pushToTalk: store.pushToTalk
    property alias spokenReplies: store.spokenReplies
    property string state_: "idle"      // idle | listening | transcribing
    readonly property bool listening: state_ === "listening"
    property bool speaking: false
    property string message: ""
    // From GET /voice/status.
    property bool listenReady: false
    property bool speakReady: false
    property bool listenDownloading: false
    property bool speakDownloading: false
    property int statusId: -1
    property int transcribeId: -1
    property int speakId: -1
    property double pressedAt: 0
    readonly property string runtimeDir: (Quickshell.env("XDG_RUNTIME_DIR") || "/tmp") + "/nexus"
    readonly property string configDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos"
    readonly property string recordBin: Quickshell.env("NEXUS_PW_RECORD") || "pw-record"
    readonly property string playBin: Quickshell.env("NEXUS_PW_PLAY") || "pw-play"

    signal heard(string text)

    function setChoice(key, value) { store[key] = value; file.writeAdapter() }
    function refresh() { if (bridge && bridge.online) statusId = bridge.api("GET", "/voice/status") }
    Component.onCompleted: refresh()
    Connections { target: voice.bridge; function onOnlineChanged() { voice.refresh() } }

    FileView {
        id: file
        path: voice.configDir + "/voice.json"
        blockLoading: true
        onLoadFailed: error => { if (error === FileViewError.FileNotFound) writeAdapter() }
        adapter: JsonAdapter {
            id: store
            property bool pushToTalk: true
            property bool spokenReplies: false
        }
    }

    // ---- push-to-talk ----
    // Super+V down / the button pressed. A second press while listening sends it.
    function press() {
        stopSpeaking();
        if (listening) { stop(); return }
        if (state_ !== "idle") return;
        if (!pushToTalk) { message = "Voice control is off (Settings, Accessibility)."; return }
        if (!bridge || !bridge.online) { message = "Nexus Core is not running."; return }
        if (!listenReady) {
            message = listenDownloading ? "The speech model is still downloading." : "Speech to text is not ready yet: its model downloads once the computer is online.";
            refresh();
            return;
        }
        message = "";
        pressedAt = Date.now();
        recorder.command = ["sh", "-c", "mkdir -p \"$1\" && chmod 700 \"$1\" && rm -f \"$1/voice.wav\" && exec \"$2\" --rate 16000 --channels 1 --format s16 \"$1/voice.wav\"",
                            "sh", runtimeDir, recordBin];
        recorder.running = true;
        state_ = "listening";
        limit.restart();
    }
    // Super+V up / the button released: a quick click keeps listening until the next press.
    function release() { if (listening && Date.now() - pressedAt > 400) stop() }
    function stop() {
        if (!listening) return;
        limit.stop();
        state_ = "transcribing";
        recorder.running = false;     // SIGTERM: pw-record finishes the WAV file and exits
    }
    function cancel() {
        limit.stop();
        if (recorder.running) recorder.running = false;
        state_ = "idle";
        Quickshell.execDetached(["rm", "-f", runtimeDir + "/voice.wav"]);
    }
    Timer { id: limit; interval: 30000; onTriggered: voice.stop() }
    Process {
        id: recorder
        onExited: (code, status) => {
            if (voice.state_ !== "transcribing") { voice.state_ = "idle"; return }    // cancelled
            if (Date.now() - voice.pressedAt < 300) { voice.state_ = "idle"; voice.message = "Hold Super+V while you speak."; return }
            voice.transcribeId = voice.bridge.api("POST", "/voice/transcribe", { audio: "voice.wav" });
            if (voice.transcribeId < 0) voice.state_ = "idle";
        }
    }

    // ---- spoken replies ----
    function speak(text) {
        if (!spokenReplies || !speakReady || !bridge || !bridge.online || !text) return;
        stopSpeaking();
        speakId = bridge.api("POST", "/voice/speak", { text: text.slice(0, 3000) });
    }
    function stopSpeaking() {
        speakId = -1;
        if (player.running) player.running = false;
        speaking = false;
    }
    Process {
        id: player
        onExited: { voice.speaking = false; Quickshell.execDetached(["rm", "-f", voice.runtimeDir + "/speech.wav"]) }
    }

    Connections {
        target: voice.bridge
        function onResult(id, type, data) {
            if (id === voice.statusId) {
                voice.statusId = -1;
                voice.listenReady = !!(data.listen && data.listen.ready);
                voice.listenDownloading = !!(data.listen && data.listen.downloading);
                voice.speakReady = !!(data.speak && data.speak.ready);
                voice.speakDownloading = !!(data.speak && data.speak.downloading);
            } else if (id === voice.transcribeId) {
                voice.transcribeId = -1; voice.state_ = "idle";
                if (data.heard && data.text) voice.heard(data.text);
                else voice.message = "Nothing was heard. Hold Super+V and speak.";
            } else if (id === voice.speakId) {
                voice.speakId = -1;
                player.command = [voice.playBin, voice.runtimeDir + "/" + (data.audio || "speech.wav")];
                player.running = true;
                voice.speaking = true;
            }
        }
        function onFailed(id, message) {
            if (id === voice.statusId) { voice.statusId = -1; voice.listenReady = false; voice.speakReady = false }
            else if (id === voice.transcribeId) { voice.transcribeId = -1; voice.state_ = "idle"; voice.message = message }
            else if (id === voice.speakId) { voice.speakId = -1; voice.message = message }
        }
    }

    IpcHandler {
        target: "voice"
        function press(): void { voice.press() }
        function release(): void { voice.stop() }
        function state(): string {
            return JSON.stringify({ state: voice.state_, speaking: voice.speaking, pushToTalk: voice.pushToTalk,
                                    spokenReplies: voice.spokenReplies, listenReady: voice.listenReady,
                                    speakReady: voice.speakReady, message: voice.message })
        }
    }

    // The microphone is open: always shown while it is, at the top of the screen.
    PanelWindow {
        visible: voice.state_ !== "idle"
        anchors.top: true
        margins.top: 12
        implicitWidth: micRow.implicitWidth + 32; implicitHeight: 40
        color: "transparent"
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.layer: WlrLayer.Top
        WlrLayershell.namespace: "nexus-microphone"
        Rectangle {
            anchors.fill: parent; radius: 20
            color: voice.listening ? "#5A0F1E" : Qt.rgba(0.04, 0.07, 0.15, 0.94)
            border.color: voice.listening ? "#FF5C7A" : "#86A8FF"; border.width: 1.5
            Row {
                id: micRow
                anchors.centerIn: parent; spacing: 10
                Rectangle {
                    anchors.verticalCenter: parent.verticalCenter
                    width: 12; height: 12; radius: 6; color: voice.listening ? "#FF5C7A" : "#86A8FF"
                    SequentialAnimation on opacity {
                        running: voice.listening; loops: Animation.Infinite
                        NumberAnimation { to: 0.35; duration: 600 } NumberAnimation { to: 1; duration: 600 }
                    }
                }
                Text {
                    anchors.verticalCenter: parent.verticalCenter
                    text: voice.listening ? "Microphone on · listening. Release Super+V (or press the microphone again) to send."
                                          : "Microphone off · turning your words into text on this computer…"
                    color: "#FFFFFF"; font.pixelSize: 13; font.weight: 550
                }
            }
            Accessible.role: Accessible.StatusBar
            Accessible.name: voice.listening ? "Microphone on, listening" : "Turning speech into text"
        }
    }
}
