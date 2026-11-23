// The clipboard history (Super+Shift+V) and emoji picker (Super+.) windows
// (Clipboard.qml, Emoji.qml). Tested with Node (tests/shell/pickers.test.mjs).

// `cliphist list`: "id<TAB>preview" per line, newest first. Images show as
// "[[ binary data 12 KiB png 800x600 ]]".
export function clips(text) {
  return String(text || "").split("\n").map(l => {
    const i = l.indexOf("\t");
    if (i < 1 || !/^\d+$/.test(l.slice(0, i))) return null;
    const preview = l.slice(i + 1);
    const image = /^\[\[ binary data .*\]\]$/.test(preview);
    return { id: l.slice(0, i), preview: image ? "Picture" + (/(\d+x\d+)/.exec(preview) ? " · " + /(\d+x\d+)/.exec(preview)[1] : "") : preview, image };
  }).filter(Boolean);
}

export function searchClips(list, query) {
  const q = String(query || "").toLowerCase();
  return q ? list.filter(c => c.preview.toLowerCase().includes(q)) : list;
}

// Unicode's emoji-test.txt: groups, and "codes ; fully-qualified # 😀 E1.0 grinning face".
export function emoji(text) {
  const out = [];
  let group = "";
  for (const line of String(text || "").split("\n")) {
    const g = /^# group: (.+)$/.exec(line);
    if (g) { group = g[1]; continue }
    const m = /^[0-9A-F ]+;\s*fully-qualified\s*#\s*(\S+)\s+E[\d.]+\s+(.+)$/.exec(line);
    // Skin-tone variants are left out: the grid shows each emoji once.
    if (m && group !== "Component" && !/skin tone/.test(m[2])) out.push({ char: m[1], name: m[2], group });
  }
  return out;
}

export function searchEmoji(list, query, group) {
  const words = String(query || "").toLowerCase().split(/\s+/).filter(Boolean);
  return list.filter(e => (!group || e.group === group) && words.every(w => e.name.toLowerCase().includes(w)));
}

export function groups(list) { return [...new Set(list.map(e => e.group))] }
