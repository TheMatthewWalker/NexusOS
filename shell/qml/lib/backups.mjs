// Settings, Backups (BackupsPage.qml): what nexus-backup says, put into words.
// Tested with Node (tests/shell/backups.test.mjs).

export const MIN_PASSWORD = 10;
// What can be restored on its own; "" is everything.
export const FOLDERS = ["Documents", "Pictures", "Desktop", "Music", "Videos", ""];

export function parse(text) {
  try { const v = JSON.parse(String(text || "").trim().split("\n").pop()); return v && typeof v === "object" ? v : {} }
  catch (e) { return {} }
}

export function sizeLabel(bytes) {
  const b = Number(bytes) || 0;
  if (b >= 1e12) return (b / 1e12).toFixed(1) + " TB";
  if (b >= 1e9) return Math.round(b / 1e9) + " GB";
  if (b >= 1e6) return Math.round(b / 1e6) + " MB";
  return Math.round(b / 1e3) + " KB";
}

function sameDay(a, b) { return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate() }
function clock(d) { return String(d.getHours()).padStart(2, "0") + ":" + String(d.getMinutes()).padStart(2, "0") }

// "today at 14:03", "yesterday at 09:10", "3 days ago", "12 March 2026".
export function whenLabel(date, now) {
  const d = date instanceof Date ? date : new Date(date);
  if (isNaN(d)) return "";
  const n = now || new Date();
  if (sameDay(d, n)) return "today at " + clock(d);
  const y = new Date(n); y.setDate(n.getDate() - 1);
  if (sameDay(d, y)) return "yesterday at " + clock(d);
  const days = Math.floor((n - d) / 86400000);
  if (days > 0 && days < 14) return days + " days ago";
  return d.getDate() + " " + ["January", "February", "March", "April", "May", "June", "July", "August", "September",
    "October", "November", "December"][d.getMonth()] + " " + d.getFullYear();
}

// The line under "Backups" for a status from `nexus-backup status`.
export function statusLine(s, now) {
  if (!s || !s.setUp) return "Not set up. Plug in a drive to keep a copy of your files on it.";
  if (s.state === "running") return "Backing up now…";
  const last = s.lastBackup ? "Last backup " + whenLabel(new Date(s.lastBackup * 1000), now) + "." : "No backup yet.";
  if (s.state === "failed") return "The last backup did not finish: " + (s.message || "it stopped") + " " + last;
  const where = s.drivePresent ? "" : " Plug in " + (s.driveName || "the backup drive") + " to back up; it then happens by itself.";
  return last + (s.unreadable ? " Some files could not be read." : "") + where;
}

// Whether backups are overdue (more than 3 days, or never).
export function overdue(s, now) {
  if (!s || !s.setUp) return false;
  const n = (now || new Date()).getTime() / 1000;
  return !s.lastBackup || n - s.lastBackup > 3 * 86400;
}

export function passwordProblem(a, b) {
  if (String(a).length < MIN_PASSWORD) return "Use a backup password of at least " + MIN_PASSWORD + " characters.";
  if (a !== b) return "The two passwords are not the same.";
  return "";
}

export function folderLabel(folder) { return folder ? folder : "Everything" }

// The --path for a folder ("" restores everything).
export function restorePath(home, folder) { return folder && /^[A-Za-z]+$/.test(folder) ? String(home).replace(/\/$/, "") + "/" + folder : "" }

export function driveLabel(d) {
  return d.name + " · " + sizeLabel(d.freeBytes) + " free of " + sizeLabel(d.totalBytes) + (d.hasBackup ? " · has a NexusOS backup" : "");
}
