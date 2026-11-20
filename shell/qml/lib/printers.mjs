// Settings, Printers (PrintersPage.qml): CUPS's lpstat output, read.
// Tested with Node (tests/shell/printers.test.mjs).

// lpstat -e: one destination per line (network printers CUPS found are included).
export function destinations(text) {
  return [...new Set(String(text || "").split("\n").map(l => l.trim()).filter(l => /^[^\s/#]+$/.test(l)))];
}

// lpstat -d: "system default destination: Office" (or lpoptions' own default for this person).
export function defaultDestination(text) {
  const m = /destination:\s*(\S+)/.exec(String(text || ""));
  return m ? m[1] : "";
}

// lpstat -p: "printer Office is idle.  enabled since ...", "printer Office now printing Office-12.",
// "printer Office disabled since ...". Unknown ones are "ready" (a found printer not yet used).
export function states(text) {
  const out = {};
  for (const line of String(text || "").split("\n")) {
    const m = /^printer (\S+) (.*)$/.exec(line.trim());
    if (!m) continue;
    const rest = m[2];
    out[m[1]] = /now printing/.test(rest) ? "printing" : /disabled/.test(rest) ? "paused" : "idle";
  }
  return out;
}

export function stateLabel(state) {
  return ({ printing: "Printing", paused: "Paused", idle: "Ready" })[state] || "Ready";
}

// A printer's name as people say it: "HP_LaserJet_M110w" -> "HP LaserJet M110w".
export function displayName(name) { return String(name || "").replace(/_/g, " ") }

// A safe destination name for lp/lpoptions (they are given as one argument, never through a shell).
export function validName(name) { return /^[A-Za-z0-9][A-Za-z0-9_.@-]{0,127}$/.test(String(name || "")) }

export function testPage(name, when) {
  return "NexusOS test page\n\nPrinter: " + displayName(name) + "\nPrinted: " + when +
    "\n\nIf you can read this, printing works.\n";
}
