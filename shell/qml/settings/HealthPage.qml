import QtQuick

// Settings, Health (feature 14, Health report card): how the system is, from
// Core's read-only probes, with the real figures; each problem links to Fix it.
Column {
    id: page
    property var theme
    property var host
    readonly property var bridge: host.bridge
    width: parent ? parent.width : 600
    spacing: 10

    property var report: null
    property string message: ""
    property bool loading: false
    property int reportId: -1
    function refresh() {
        if (!bridge.online) { message = "Nexus Core is not running."; return }
        loading = true; message = "";
        reportId = bridge.api("GET", "/health/report");
    }
    Component.onCompleted: refresh()
    Connections {
        target: page.bridge
        function onResult(id, type, data) { if (id === page.reportId) { page.loading = false; page.report = data } }
        function onFailed(id, message) { if (id === page.reportId) { page.loading = false; page.message = message } }
    }
    readonly property var dots: ({ ok: "#7FE0B0", failed: "#FF8FA3", timeout: "#FFA65C", unavailable: "#9AA9C9" })

    PageHeader { theme: page.theme; title: "Health"
                 summary: "How this computer is doing, from read-only checks. Problems link to Fix it." }
    Row {
        spacing: 8
        Button { theme: page.theme; label: page.loading ? "Checking…" : "Check again"; enabled: !page.loading; onClicked: page.refresh() }
        Button { theme: page.theme; primary: true; label: "Fix a problem…"; onClicked: page.host.fixIt("") }
    }
    Text { visible: page.message !== ""; text: page.message; color: page.theme.inkSecondary; font.pixelSize: 13 }
    Text { visible: !!page.report; color: page.theme.inkSecondary; font.pixelSize: 12
           text: page.report ? "Checked " + Qt.formatDateTime(new Date(page.report.generatedAt), "ddd d MMM, HH:mm") : "" }

    Repeater {
        model: page.report ? page.report.problems : []
        delegate: SettingRow {
            required property var modelData
            theme: page.theme; highlighted: true
            label: "Problem · " + modelData.area; detail: modelData.cause
            Button { theme: page.theme; primary: true; label: "Fix it"; onClicked: page.host.fixIt(modelData.area + ": " + modelData.cause) }
        }
    }
    Repeater {
        model: page.report ? page.report.facts : []
        delegate: Rectangle {
            required property var modelData
            width: page.width; height: factText.implicitHeight + 24; radius: 10; color: page.theme.chip
            Rectangle { x: 14; y: 18; width: 8; height: 8; radius: 4; color: page.dots[modelData.status] || "#9AA9C9" }
            Column {
                id: factText
                x: 32; y: 12; width: parent.width - 48; spacing: 2
                Text { text: modelData.area.charAt(0).toUpperCase() + modelData.area.slice(1); color: page.theme.ink; font.pixelSize: 13; font.weight: 500 }
                Text { width: parent.width; wrapMode: Text.Wrap; textFormat: Text.PlainText; text: modelData.text
                       color: page.theme.inkSecondary; font.pixelSize: 12 }
            }
        }
    }
}
