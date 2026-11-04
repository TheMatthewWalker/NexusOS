// The graphical installer's logic (Installer.qml): what each step accepts,
// the flags it gives nexus-install, and what nexus-install's --progress-json
// lines mean. Plain JavaScript so it runs in QML and is tested with Node
// (tests/shell/installer.test.mjs). The rules match nix/installer/nexus-install.sh,
// which checks everything again.

export const STEPS = ["welcome", "disk", "you", "time", "summary", "install", "done"];
export const STEP_LABELS = { welcome: "Welcome", disk: "Disk", you: "You", time: "Time zone", summary: "Summary", install: "Install", done: "Done" };
export const MIN_PASSWORD = 10;

// Sizes as Windows' Disk Management shows them (1 GB = 1024 MB), like nexus-install.
export function sizeLabel(bytes) {
  const n = Number(bytes);
  if (!(n >= 0)) return "";
  const gib = n / 1073741824;
  if (gib >= 1024) return (gib / 1024).toFixed(gib >= 10240 ? 0 : 1) + " TB";
  if (gib >= 10) return Math.round(gib) + " GB";
  if (gib >= 1) return gib.toFixed(1) + " GB";
  return Math.round(n / 1048576) + " MB";
}

const TRANSPORTS = { nvme: "NVMe", sata: "SATA", ata: "SATA", usb: "USB", mmc: "memory card", virtio: "virtual", scsi: "SCSI" };
export function diskTitle(d) { return (d.model || d.path) + " (" + sizeLabel(d.size) + ")" }
export function diskDetail(d) {
  const kind = TRANSPORTS[d.transport] || (d.path.includes("vd") ? "virtual" : "");
  const parts = [d.path];
  if (kind) parts.push(kind + (d.removable ? ", removable" : ""));
  if (d.inUse) parts.push("the installer is running from it");
  return parts.join(" · ");
}

// What can be done with a disk: { erase: {possible, reason}, alongside: {possible, reason, warnings, systems} }.
export function diskChoices(d, minimumBytes) {
  if (!d) return { erase: { possible: false, reason: "" }, alongside: { possible: false, reason: "", warnings: [] } };
  const erase = d.inUse ? { possible: false, reason: "The installer is running from this disk." }
              : d.size < minimumBytes ? { possible: false, reason: "This disk is too small: NexusOS needs at least " + sizeLabel(minimumBytes) + "." }
              : { possible: true, reason: "" };
  const a = d.alongside;
  const alongside = d.inUse || !a ? { possible: false, reason: d.inUse ? erase.reason : "", warnings: [] }
                  : { possible: !!a.possible, reason: a.reason || "", warnings: a.warnings || [], systems: a.systems || "", esp: a.esp || "", freeBytes: a.freeBytes || 0 };
  return { erase, alongside };
}

// Login names: lower case letters, digits, - and _, starting with a letter.
export function loginProblem(name) {
  if (!name) return "Choose a login name.";
  if (!/^[a-z_][a-z0-9_-]{0,31}$/.test(name)) return "Use lower case letters, digits, - and _, starting with a letter.";
  if (["root", "nixos", "nobody"].includes(name)) return name + " is reserved; pick another name.";
  return "";
}
// A login name from a full name: "Ada Lovelace" -> "ada".
export function suggestLogin(fullName) {
  const first = String(fullName || "").trim().split(/\s+/)[0] || "";
  const plain = first.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9_-]/g, "");
  const name = plain.replace(/^[^a-z_]+/, "").slice(0, 32);
  return loginProblem(name) ? "" : name;
}
export function passwordProblem(password, again) {
  if (password.length < MIN_PASSWORD) return "Use at least " + MIN_PASSWORD + " characters: it also unlocks the disk and approves system changes.";
  if (password !== again) return "The passwords do not match.";
  return "";
}
export function hostnameProblem(host) {
  return /^[A-Za-z0-9][A-Za-z0-9-]{0,62}$/.test(host) ? "" : "Computer names use letters, digits and -, up to 63 characters.";
}
// Full names go into the configuration as a string; nexus-install escapes it. Keep it to one line.
export function fullNameProblem(name) { return /[\x00-\x1f\x7f]/.test(name) ? "Use one line of text." : "" }

export function youProblems(f) {
  return { fullName: fullNameProblem(f.fullName), login: loginProblem(f.login),
           password: passwordProblem(f.password, f.again), host: hostnameProblem(f.host) };
}
export function youReady(f) { return Object.values(youProblems(f)).every(p => p === "") }

export function filterZones(zones, query) {
  const q = String(query || "").trim().toLowerCase().replace(/\s+/g, "_");
  return q ? zones.filter(z => z.toLowerCase().includes(q)) : zones;
}
export function zoneLabel(zone) { return String(zone).replace(/_/g, " ").replace(/\//g, " / ") }

// The flags for nexus-install. The password goes on standard input (--password-file -), never on the command line.
export function installArgs(s) {
  const args = ["--progress-json", "--yes", "--disk", s.disk, "--user", s.login, "--password-file", "-",
                "--hostname", s.host, "--timezone", s.timezone];
  if (s.fullName) args.push("--full-name", s.fullName);
  if (s.alongside) args.push("--alongside");
  if (!s.encrypt) args.push("--no-encrypt");
  return args;
}

// One line from nexus-install --progress-json or --probe-json, or null if it is not one.
export function parseLine(line) {
  const t = String(line || "").trim();
  if (!t.startsWith("{")) return null;
  try { const o = JSON.parse(t); return o && typeof o.event === "string" ? o : null } catch (e) { return null }
}

export function initialProgress() {
  return { phase: "idle", n: 0, of: 1, title: "", detail: "", error: "", log: "", tail: [], fraction: 0 };
}
// The progress so far, after one event. Steps fill the bar evenly; the long
// "install" step creeps forward with its output so the bar never looks stuck.
export function applyEvent(p, ev) {
  const q = Object.assign({}, p);
  switch (ev.event) {
    case "start": q.phase = "running"; q.log = ev.log || q.log; break;
    case "step":
      q.phase = "running"; q.n = ev.n; q.of = ev.of; q.title = ev.title; q.detail = ""; q.lines = 0;
      q.fraction = Math.max(q.fraction, (ev.n - 1) / ev.of);
      break;
    case "detail":
      q.detail = ev.text; q.lines = (q.lines || 0) + 1;
      q.fraction = Math.min((q.n) / q.of - 0.01, Math.max(q.fraction, (q.n - 1 + 0.9 * (1 - 1 / (1 + q.lines / 400))) / q.of));
      break;
    case "done": q.phase = "done"; q.fraction = 1; q.title = "NexusOS is installed"; q.detail = ""; break;
    case "error":
      q.phase = "failed"; q.error = ev.message || "The installer stopped."; q.log = ev.log || q.log; q.tail = ev.tail || [];
      break;
  }
  return q;
}
// When nexus-install exits without saying why (killed, pkexec refused).
export function exitProblem(code, stderr) {
  if (code === 126 || code === 127) return "The installer was not allowed to run as administrator (pkexec: " + (stderr || "refused") + ").";
  return "The installer stopped unexpectedly (exit " + code + ")." + (stderr ? " " + stderr : "");
}

// The Summary step's lines, in order.
export function summaryRows(s, probe) {
  const d = s.diskInfo || { path: s.disk, size: 0 };
  const hw = (probe && probe.hardware) || {};
  const disk = s.alongside
    ? "Alongside " + (s.systems || "the system already there") + " on " + diskTitle(d) + ", in " + sizeLabel(s.freeBytes) +
      " of free space. Nothing else is erased; the start-up menu lists Windows too."
    : "Erase " + diskTitle(d) + ": everything on it is deleted.";
  return [
    { label: "Disk", value: disk, danger: !s.alongside },
    { label: "Encryption", value: s.encrypt ? "On: your password unlocks the disk when the computer starts" : "Off: anyone with the disk can read it" },
    { label: "You", value: (s.fullName ? s.fullName + " (" + s.login + ")" : s.login) },
    { label: "Computer", value: s.host },
    { label: "Time zone", value: zoneLabel(s.timezone) },
    { label: "Processor", value: hw.processor || "unknown" },
    { label: "Graphics", value: hw.graphics || "unknown" },
    { label: "Local AI", value: hw.localAI || "unknown" },
  ];
}
