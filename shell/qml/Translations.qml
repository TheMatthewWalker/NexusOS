import QtQuick
import Quickshell
import Quickshell.Io
import "lib/i18n.mjs" as I

// The shell in the person's language: the catalogue for LANGUAGE/LANG
// (set at login from Settings, Language and region) from shell/qml/i18n.
// t("English text") is the translation, or the English text when there is
// none yet, so a partly translated shell is always complete.
Scope {
    id: tr
    readonly property string language: I.pick(Quickshell.env("LANGUAGE"), Quickshell.env("LANG"))
    property var catalogue: ({})
    function t(text, args) { return I.t(catalogue, text, args) }

    FileView {
        path: tr.language ? Quickshell.shellDir + "/i18n/" + tr.language + ".json" : ""
        onLoaded: { try { tr.catalogue = JSON.parse(text()) } catch (e) { tr.catalogue = ({}) } }
    }
}
