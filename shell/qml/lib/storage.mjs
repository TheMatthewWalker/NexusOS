// Settings, Storage and Default apps (StoragePage.qml, DefaultAppsPage.qml).
// Tested with Node (tests/shell/storage.test.mjs).

export function parse(text) {
  try { const v = JSON.parse(String(text || "").trim().split("\n").pop()); return v && typeof v === "object" ? v : {} }
  catch (e) { return {} }
}

export function sizeLabel(bytes) {
  const b = Number(bytes) || 0;
  if (b >= 1e12) return (b / 1e12).toFixed(1) + " TB";
  if (b >= 1e10) return Math.round(b / 1e9) + " GB";
  if (b >= 1e9) return (b / 1e9).toFixed(1) + " GB";
  if (b >= 1e6) return Math.round(b / 1e6) + " MB";
  return b > 0 ? Math.max(1, Math.round(b / 1e3)) + " KB" : "nothing";
}

// The main disk, in words, and whether it is nearly full (under 10% or 10 GB free).
export function diskLine(v) {
  if (!v) return "…";
  const used = v.totalBytes - v.freeBytes;
  return sizeLabel(v.freeBytes) + " free of " + sizeLabel(v.totalBytes) + " (" + Math.round(100 * used / Math.max(1, v.totalBytes)) + "% used)";
}
export function low(v) { return !!v && (v.freeBytes < 10e9 || v.freeBytes / Math.max(1, v.totalBytes) < 0.1) }

// Each kind as a share of the disk, biggest first, for the bar.
export function shares(kinds, total) {
  return (kinds || []).slice().sort((a, b) => b.bytes - a.bytes)
    .map(k => ({ label: k.label, path: k.path, bytes: k.bytes, share: total > 0 ? k.bytes / total : 0 }));
}

export function freed(r) { return r && r.freedBytes ? " " + sizeLabel(r.freedBytes) + " freed." : "" }
