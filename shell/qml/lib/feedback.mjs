// Settings, Feedback (FeedbackPage.qml) and the crash notices (CrashWatcher.qml).
// Nothing is ever sent by itself: a report is shown in full, and sending it
// opens a new GitHub issue in the browser, which the person submits (or not).
// Tested with Node (tests/shell/feedback.test.mjs).

export const ISSUES = "https://github.com/TheMatthewWalker/NexusOS/issues/new";
const MAX_URL = 7000;          // browsers and GitHub accept about 8 KB in an address
const SIGNALS = { 4: "SIGILL", 5: "SIGTRAP", 6: "SIGABRT", 7: "SIGBUS", 8: "SIGFPE", 11: "SIGSEGV", 31: "SIGSYS" };

export function signalName(n) { return SIGNALS[n] || ("signal " + n) }

// A program's name as a report shows it: the package for /nix/store paths
// (firefox-133.0 for /nix/store/<hash>-firefox-133.0/bin/.firefox-wrapped), and
// never a path in someone's home folder.
export function exeLabel(exe, home) {
  const p = String(exe || "");
  const store = /^\/nix\/store\/[0-9a-z]{32}-([^/]+)\/.*?([^/]+)$/.exec(p);
  if (store) return store[2].replace(/^\.(.*)-wrapped$/, "$1") + " (" + store[1] + ")";
  const name = p.split("/").pop() || "a program";
  if (home && (p === home || p.startsWith(String(home).replace(/\/$/, "") + "/"))) return name + " (from your home folder)";
  return name;
}

// coredumpctl --json=short list: [{time (µs), pid, uid, sig, exe, ...}], newest first.
export function crashes(text, home) {
  let list;
  try { list = JSON.parse(String(text || "")) } catch (e) { return [] }
  if (!Array.isArray(list)) return [];
  return list.filter(c => c && c.exe && c.time).map(c => ({
    id: String(c.pid) + "-" + c.time, when: Math.floor(Number(c.time) / 1000), program: exeLabel(c.exe, home),
    name: String(c.exe).split("/").pop().replace(/^\.(.*)-wrapped$/, "$1"), signal: signalName(c.sig)
  })).sort((a, b) => b.when - a.when);
}

// The new crashes since `since` (ms), for a notice.
export function newSince(list, since) { return (list || []).filter(c => c.when > since) }

export function report(r) {
  const lines = [];
  lines.push(String(r.text || "").trim() || "(No description given.)");
  if (r.crash) {
    lines.push("", "### Crash", "- Program: " + r.crash.program, "- Stopped with: " + r.crash.signal,
               "- When: " + new Date(r.crash.when).toISOString());
  }
  if (r.system) {
    lines.push("", "### This computer");
    for (const [k, v] of Object.entries(r.system)) if (v) lines.push("- " + k + ": " + v);
  }
  lines.push("", "_Sent from Settings, Feedback; the person read this report before sending it._");
  const first = String(r.text || "").trim().split("\n")[0];
  const title = (r.crash && !first ? r.crash.name + " stopped unexpectedly" : first || "Feedback").slice(0, 120);
  return { title, body: lines.join("\n") };
}

// The new-issue address, with the body shortened if it would be too long.
export function issueUrl(title, body) {
  const make = b => ISSUES + "?title=" + encodeURIComponent(title) + "&body=" + encodeURIComponent(b);
  let b = String(body);
  while (make(b).length > MAX_URL && b.length > 200) b = b.slice(0, Math.floor(b.length * 0.9)) + "\n…";
  return make(b);
}
