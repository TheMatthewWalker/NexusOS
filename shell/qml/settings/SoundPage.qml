import QtQuick
import Quickshell.Services.Pipewire

// Settings, Sound: output and input volume, mute, and which device plays.
// Everything is read from and written to PipeWire; a missing device says so.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property var sink: Pipewire.defaultAudioSink
    readonly property var source: Pipewire.defaultAudioSource
    readonly property var outputs: Pipewire.nodes.values.filter(n => n.isSink && !n.isStream && n.audio)
    // Apps playing sound now, each with its own volume, as Windows' volume mixer.
    readonly property var playing: Pipewire.nodes.values.filter(n => n.isStream && n.audio &&
                                                               (n.properties || {})["media.class"] === "Stream/Output/Audio")
    PwObjectTracker { objects: [page.sink, page.source].filter(x => x).concat(page.playing) }
    function appName(n) { const p = n.properties || {}; return p["application.name"] || p["application.process.binary"] || n.name || "An app" }
    readonly property bool hasOut: !!(sink && sink.audio)
    readonly property bool hasIn: !!(source && source.audio)
    function deviceName(n) { return n ? (n.description || n.nickname || n.name || "Unknown device") : "" }

    PageHeader { theme: page.theme; title: "Sound"; summary: "What you hear and what your microphone picks up." }

    SettingRow {
        theme: page.theme; label: "Volume"; highlighted: page.host.highlight === "volume"
        detail: !page.hasOut ? "No output device found." : page.sink.audio.muted ? "Muted · " + page.deviceName(page.sink)
              : Math.round(page.sink.audio.volume * 100) + "% · " + page.deviceName(page.sink)
        Row {
            spacing: 14
            Slider { theme: page.theme; name: "Volume"; enabled: page.hasOut; anchors.verticalCenter: parent.verticalCenter
                     value: page.hasOut ? page.sink.audio.volume : 0
                     onMoved: v => { page.sink.audio.volume = v; if (v > 0) page.sink.audio.muted = false } }
            Switch { theme: page.theme; name: "Sound on"; enabled: page.hasOut; anchors.verticalCenter: parent.verticalCenter
                     checked: page.hasOut && !page.sink.audio.muted; onToggled: page.sink.audio.muted = !page.sink.audio.muted }
        }
    }
    SettingRow {
        theme: page.theme; label: "Apps"; highlighted: page.host.highlight === "apps"
        detail: page.playing.length === 0 ? "Apps playing sound appear here, each with its own volume." : "Each app's own volume."
    }
    Repeater {
        model: page.playing
        delegate: SettingRow {
            required property var modelData
            theme: page.theme; label: page.appName(modelData)
            detail: modelData.audio ? (modelData.audio.muted ? "Muted" : Math.round(modelData.audio.volume * 100) + "%") : ""
            Row {
                spacing: 14
                Slider { theme: page.theme; name: page.appName(modelData) + " volume"; anchors.verticalCenter: parent.verticalCenter
                         value: modelData.audio ? modelData.audio.volume : 0
                         onMoved: v => { if (modelData.audio) { modelData.audio.volume = v; if (v > 0) modelData.audio.muted = false } } }
                Switch { theme: page.theme; name: page.appName(modelData) + " sound on"; anchors.verticalCenter: parent.verticalCenter
                         checked: !!modelData.audio && !modelData.audio.muted
                         onToggled: if (modelData.audio) modelData.audio.muted = !modelData.audio.muted }
            }
        }
    }
    SettingRow {
        theme: page.theme; label: "Output device"; highlighted: page.host.highlight === "output"
        visible: page.outputs.length > 1
        detail: "Where sound plays."
    }
    Repeater {
        model: page.outputs.length > 1 ? page.outputs : []
        delegate: Rectangle {
            required property var modelData
            readonly property bool on: modelData === page.sink
            width: page.width; height: 40; radius: 9
            color: on || hover.hovered ? Qt.rgba(0.49, 0.42, 1.0, 0.16) : "transparent"
            border.color: on ? page.theme.focus : "transparent"
            Text { x: 16; anchors.verticalCenter: parent.verticalCenter; text: page.deviceName(modelData)
                   color: page.theme.ink; font.pixelSize: 13; width: parent.width - 100; elide: Text.ElideRight }
            Text { anchors { right: parent.right; rightMargin: 16; verticalCenter: parent.verticalCenter }
                   visible: on; text: "In use"; color: page.theme.inkSecondary; font.pixelSize: 12 }
            HoverHandler { id: hover; cursorShape: Qt.PointingHandCursor }
            TapHandler { onTapped: Pipewire.preferredDefaultAudioSink = modelData }
            Accessible.role: Accessible.RadioButton
            Accessible.checked: on
            Accessible.name: page.deviceName(modelData)
        }
    }
    SettingRow {
        theme: page.theme; label: "Microphone"; highlighted: page.host.highlight === "microphone"
        detail: !page.hasIn ? "No microphone found." : page.source.audio.muted ? "Muted · " + page.deviceName(page.source)
              : Math.round(page.source.audio.volume * 100) + "% · " + page.deviceName(page.source)
        Row {
            spacing: 14
            Slider { theme: page.theme; name: "Microphone volume"; enabled: page.hasIn; anchors.verticalCenter: parent.verticalCenter
                     value: page.hasIn ? page.source.audio.volume : 0; onMoved: v => page.source.audio.volume = v }
            Switch { theme: page.theme; name: "Microphone on"; enabled: page.hasIn; anchors.verticalCenter: parent.verticalCenter
                     checked: page.hasIn && !page.source.audio.muted; onToggled: page.source.audio.muted = !page.source.audio.muted }
        }
    }
}
