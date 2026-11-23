import QtQuick
import Quickshell
import Quickshell.Io
import "../lib/region.mjs" as R

// Settings, Language and region: your language, how dates, numbers and money
// are written, your time zone, and extra keyboard layouts. All are yours
// alone and need no password. Language, formats and time zone are kept in
// ~/.config/nexusos/region.env, which the session reads at login (so they
// take effect when you log in again); keyboard layouts apply at once.
Column {
    id: page
    property var theme
    property var host
    width: parent ? parent.width : 600
    spacing: 10

    readonly property string configDir: (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos"
    property var locales: []
    property var keyboards: ({})
    property string zoneText: ""
    property var choice: ({ language: "", formats: "", timeZone: "" })
    // What the session started with: a change differs from it until the next login.
    readonly property var running: ({ language: Quickshell.env("LANG") || "", formats: Quickshell.env("LC_TIME") || Quickshell.env("LANG") || "",
                                      timeZone: Quickshell.env("TZ") || "" })
    readonly property bool pending: (choice.language && choice.language !== running.language) ||
                                    (choice.formats && choice.formats !== running.formats) ||
                                    (choice.timeZone && choice.timeZone !== running.timeZone)
    property string picking: ""          // "language", "formats" or "timezone"
    property string zoneQuery: ""
    property string layoutQuery: ""

    FileView { path: "/etc/nexus/locales.json"; onLoaded: { try { page.locales = JSON.parse(text()) } catch (e) {} } }
    FileView { path: "/etc/nexus/keyboards.json"; onLoaded: { try { page.keyboards = JSON.parse(text()) } catch (e) {} } }
    FileView {
        id: regionFile
        path: page.configDir + "/region.env"
        onLoaded: page.choice = R.parseEnv(text())
    }
    Process { id: zoneLister; command: ["timedatectl", "list-timezones"]; running: true
              stdout: StdioCollector { onStreamFinished: page.zoneText = text } }

    function choose(key, value) {
        const next = Object.assign({}, choice);
        next[key] = value;
        choice = next;
        regionFile.setText(R.envFile(next));
        picking = "";
    }
    readonly property var layoutExtras: (host.desktop.keyboardLayouts || "").split(",").filter(Boolean)
    readonly property var layoutMatches: {
        const q = layoutQuery.toLowerCase();
        if (!q) return [];
        return Object.keys(keyboards).filter(k => k !== host.desktop.baseLayout && !layoutExtras.includes(k) &&
                                              (k === q || (keyboards[k].name || "").toLowerCase().includes(q))).slice(0, 6);
    }
    function layoutName(k) { return keyboards[k] ? keyboards[k].name : k }

    PageHeader { theme: page.theme; title: "Language and region"; summary: "Your language, how dates and numbers are written, your time zone and keyboard layouts." }

    Text {
        visible: page.pending
        width: page.width; wrapMode: Text.Wrap; color: page.theme.inkSecondary; font.pixelSize: 13
        text: "Log out and back in to use the new language, formats or time zone everywhere."
    }

    SettingRow {
        theme: page.theme; label: "Language"; highlighted: page.host.highlight === "language"
        detail: R.localeName(page.locales, page.choice.language || page.running.language) +
            ". Menus and Settings follow it where they are translated, and apps follow it too; Nexus answers in the language you write in."
        Button { theme: page.theme; label: page.picking === "language" ? "Close" : "Change"; onClicked: page.picking = page.picking === "language" ? "" : "language" }
    }
    SettingRow {
        theme: page.theme; label: "Formats"; highlighted: page.host.highlight === "formats"
        detail: {
            const l = Qt.locale((page.choice.formats || page.running.formats || "en_GB").replace(".UTF-8", ""));
            return R.localeName(page.locales, page.choice.formats || page.running.formats) + ": " +
                new Date(2026, 2, 14, 15, 30).toLocaleString(l, Locale.ShortFormat) + " · " + Number(1234.5).toLocaleString(l, "f", 2) +
                " · " + Number(9.99).toLocaleCurrencyString(l);
        }
        Button { theme: page.theme; label: page.picking === "formats" ? "Close" : "Change"; onClicked: page.picking = page.picking === "formats" ? "" : "formats" }
    }
    Flow {
        visible: page.picking === "language" || page.picking === "formats"
        width: page.width; spacing: 6
        Repeater {
            model: page.picking === "language" || page.picking === "formats" ? page.locales : []
            delegate: Button {
                required property var modelData
                theme: page.theme; label: modelData.name
                onClicked: page.choose(page.picking, modelData.locale)
            }
        }
    }

    SettingRow {
        theme: page.theme; label: "Time zone"; highlighted: page.host.highlight === "timezone"
        detail: R.zoneLabel(page.choice.timeZone || page.running.timeZone || "the computer's own") + ". The clock shows this time zone's time."
        Button { theme: page.theme; label: page.picking === "timezone" ? "Close" : "Change"; onClicked: page.picking = page.picking === "timezone" ? "" : "timezone" }
    }
    Column {
        visible: page.picking === "timezone"
        spacing: 6
        TextField { theme: page.theme; label: "Search for a city"; placeholder: "London, Paris, New York…"; onTextChanged: page.zoneQuery = text }
        Flow {
            width: page.width; spacing: 6
            Repeater {
                model: page.zoneQuery.length >= 2 ? R.zones(page.zoneText, page.zoneQuery) : []
                delegate: Button { required property var modelData; theme: page.theme; label: R.zoneLabel(modelData); onClicked: page.choose("timeZone", modelData) }
            }
        }
        Button { theme: page.theme; label: "Use the computer's own"; onClicked: page.choose("timeZone", "") }
    }

    SettingRow {
        theme: page.theme; label: "Chinese, Japanese and Korean typing"; highlighted: page.host.highlight === "inputmethod"
        detail: (R.inputMethodOn(Object.assign({ language: page.running.language }, page.choice))
                    ? "On from the next login: Ctrl+Space switches between your keyboard and Pinyin, Mozc (Japanese) or Hangul."
                    : "Off. Turn it on to type Chinese, Japanese or Korean.") +
                " Automatic turns it on when your language is one of them."
        Choice { theme: page.theme; name: "Chinese, Japanese and Korean typing"; current: page.choice.input || ""
                 options: [{ value: "", label: "Automatic" }, { value: "fcitx5", label: "On" }, { value: "none", label: "Off" }]
                 onChose: v => page.choose("input", v) }
    }
    SettingRow {
        theme: page.theme; label: "Keyboard layouts"; highlighted: page.host.highlight === "keyboards"
        detail: [page.layoutName(page.host.desktop.baseLayout)].concat(page.layoutExtras.map(page.layoutName)).join(", ") +
            (page.layoutExtras.length ? ". Alt+Shift switches between them." : ". Add another to type in more than one language.")
    }
    Flow {
        width: page.width; spacing: 6
        Repeater {
            model: page.layoutExtras
            delegate: Button {
                required property var modelData
                theme: page.theme; label: "Remove " + page.layoutName(modelData)
                onClicked: page.host.desktop.set("keyboardLayouts", R.removeLayout(page.host.desktop.keyboardLayouts, modelData))
            }
        }
    }
    TextField {
        visible: page.layoutExtras.length < 3
        theme: page.theme; label: "Add a layout"; placeholder: "French, German, Spanish…"
        onTextChanged: page.layoutQuery = text
    }
    Flow {
        width: page.width; spacing: 6
        Repeater {
            model: page.layoutMatches
            delegate: Button {
                required property var modelData
                theme: page.theme; label: page.layoutName(modelData)
                onClicked: page.host.desktop.set("keyboardLayouts", R.addLayout(page.host.desktop.keyboardLayouts, modelData, page.host.desktop.baseLayout))
            }
        }
    }
}
