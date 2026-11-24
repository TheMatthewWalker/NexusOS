// The top bar's microphone, camera and screen-sharing indicators (TopBar.qml),
// from PipeWire's nodes: an app recording has a capture stream
// ("Stream/Input/Audio" for a microphone, "Stream/Input/Video" for a camera or
// a shared screen), and a shared screen is a video source made by the
// screen-sharing portal (xdg-desktop-portal-hyprland, "xdph...").
// Tested with Node (tests/shell/privacy.test.mjs).

function appOf(p) {
  return p["application.name"] || p["application.process.binary"] || p["node.name"] || "An app";
}

// nodes: [{ properties }] -> { microphone: [apps], camera: [apps], screen: bool }
export function inUse(nodes, self) {
  const mic = new Set(), cam = new Set();
  let screen = false;
  for (const n of nodes || []) {
    const p = (n && n.properties) || {};
    const cls = p["media.class"] || "";
    const name = String(p["node.name"] || "");
    if (cls === "Video/Source" && /^xdph/i.test(name)) screen = true;
    if (cls === "Stream/Input/Audio") { const a = appOf(p); if (a !== self) mic.add(a) }
    if (cls === "Stream/Input/Video") cam.add(appOf(p));
  }
  // An app receiving a shared screen also has a video stream: it is sharing, not filming.
  return { microphone: [...mic], camera: screen ? [] : [...cam], screen };
}

export function describe(u) {
  const parts = [];
  if (u.microphone.length) parts.push("Microphone in use by " + u.microphone.join(", "));
  if (u.camera.length) parts.push("Camera in use by " + u.camera.join(", "));
  if (u.screen) parts.push("Your screen is being shared");
  return parts.join(". ");
}

// The player to show: one that is playing, else the last one there.
export function pick(players) {
  const list = (players || []).filter(p => p);
  return list.find(p => p.isPlaying) || list[list.length - 1] || null;
}

export function nowPlaying(p) {
  if (!p) return "";
  const t = String(p.trackTitle || "").trim(), a = String(p.trackArtist || "").trim();
  return t ? (a ? t + " · " + a : t) : String(p.identity || "");
}

// Virus scanning on demand (nix/modules/malware-scan.nix): a Guardian change to turn on.
export const SCAN_PLAN = {
  description: "Scan files for viruses on demand (ClamAV)",
  changes: [{ option: "nexusos.malwareScan.enable", enabled: true }]
};
