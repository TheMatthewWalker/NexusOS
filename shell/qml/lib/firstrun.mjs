// First-run setup (feature 4): which curated bundles fit what the person
// said, and the one list of apps, system items and settings they add up to.
// Only items from the bundles can appear: free text picks bundles, it never
// names packages.

// Words in the text that are keywords of a bundle; returns the bundle ids in
// bundle order, and the words that matched (to show why).
export function match(text, bundles) {
    const said = " " + String(text || "").toLowerCase().replace(/[\s,.;:!?()"'\/&-]+/g, " ") + " ";
    const ids = [], words = [];
    for (const b of bundles) {
        const hit = b.keywords.filter(k => said.includes(" " + k.toLowerCase() + " "));
        if (hit.length) { ids.push(b.id); words.push(...hit.filter(w => !words.includes(w))) }
    }
    return { ids, words };
}

export function itemKey(kind, item) {
    if (kind === "apps") return "app:" + item.flathub;
    if (kind === "system") return item.package ? "pkg:" + item.package : "opt:" + item.option;
    return "set:" + item.key;
}

// The chosen bundles merged: each item once (first bundle wins), tagged with
// the bundle it came from, minus the ones the person removed.
export function merge(ids, bundles, removed) {
    const out = { apps: [], system: [], settings: [] };
    const seen = new Set(removed || []);
    for (const b of bundles.filter(b => ids.includes(b.id))) {
        for (const kind of ["apps", "system", "settings"])
            for (const item of b[kind]) {
                const key = itemKey(kind, item);
                if (seen.has(key)) continue;
                seen.add(key);
                out[kind].push(Object.assign({}, item, { id: key, from: b.label }));
            }
    }
    return out;
}

// The one Guardian plan for the system items: packages and options, typed.
export function systemPlan(items, labels) {
    if (!items.length) return null;
    return {
        description: ("First-run setup (" + labels.join(", ") + "): " + items.map(i => i.name).join(", ")).slice(0, 480),
        packages: items.filter(i => i.package).map(i => i.package),
        changes: items.filter(i => i.option).map(i => ({ option: i.option, enabled: i.enabled }))
    };
}
