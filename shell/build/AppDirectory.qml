import QtQuick
import Quickshell
import Quickshell.Io

// Maps SpaceUI app roles (files, browser, terminal, studio, models, settings)
// to installed desktop entries. The mapping comes from the NixOS module
// (/etc/nexus/desktop-apps.json, or NEXUS_DESKTOP_APPS), e.g.
//   {"files": "thunar", "terminal": "kitty", "browser": "firefox"}
// A role with no installed app stays visible but says so; nothing is faked.
Item {
    id: directory
    property var mapping: ({})
    readonly property string path: Quickshell.env("NEXUS_DESKTOP_APPS") || "/etc/nexus/desktop-apps.json"

    FileView {
        path: directory.path
        onLoaded: {
            try { directory.mapping = JSON.parse(text()) }
            catch (e) { console.warn("nexus-shell: ignoring malformed " + directory.path) }
        }
    }

    function entryFor(role) {
        // Read the application list so bindings re-check when entries finish loading.
        void DesktopEntries.applications.values.length;
        const id = mapping[role];
        if (!id) return null;
        return DesktopEntries.byId(String(id).replace(/\.desktop$/, "")) || null;
    }
    function installed(role) { return entryFor(role) !== null }
    function open(role) {
        const entry = entryFor(role);
        if (!entry) return false;
        entry.execute();
        return true;
    }
    // Best icon for an open window: its desktop entry's icon, if there is one.
    function iconFor(appId) {
        const entry = DesktopEntries.heuristicLookup(appId);
        return entry && entry.icon ? Quickshell.iconPath(entry.icon, true) : "";
    }
}
