// What the Guardian dialog shows at approval, and which polkit requests it
// answers. Plain JavaScript so it runs in QML and is tested with Node
// (tests/shell/guardian.test.mjs). Guardian.qml supplies the data.
//
// The record is the broker's own description of a checked candidate
// (GET /guardian/proposals/{id}/describe, the broker's "describe"):
//   { candidateId, buildHash, settings: [{ option, summary, before, after }],
//     packages: [{ package, before }], risk: { level, notes }, staged }
// It, not Core's proposal text, is what the person approves; Core's own
// summary is only shown as "Nexus says". A security update's record has kind
// "update", no settings or packages, and the signed update instead:
//   update: { lockHash, previousLockHash, nixpkgs: { branch, before, after: { rev, lastModified } },
//             packages: [{ name, label, before, after }] | null, otherPackages, added, removed,
//             nexusos (a NexusOS update): { source, before, after: { rev, lastModified } } }

export const APPROVE_ACTION = "org.nexusos.guardian.approve";

export function isRecord(r) {
  return !!r && typeof r === "object" && /^[0-9a-f]{24}$/.test(r.candidateId || "") &&
    /^[0-9a-f]{64}$/.test(r.buildHash || "") && Array.isArray(r.settings) && Array.isArray(r.packages);
}

export function isUpdate(r) {
  return isRecord(r) && r.kind === "update" && !!r.update && typeof r.update === "object" &&
    /^[0-9a-f]{64}$/.test(r.update.lockHash || "");
}

// How many package versions an update changes (listed plus the others).
export function updatedCount(u) {
  if (!u || !Array.isArray(u.packages)) return 0;
  return u.packages.length + (Number.isInteger(u.otherPackages) ? u.otherPackages : 0);
}

function label(p) { return p.label || p.name }

// "Security updates: 12 packages updated, including Linux 6.18.56 → 6.18.60, Firefox 144.0 → 144.0.2"
export function updateLine(u, shown = 3) {
  if (!u || typeof u !== "object") return "";
  const what = isNexusUpdate(u) ? "NexusOS " + day(u.nexusos.after.lastModified) + ", with security updates" : "Security updates";
  if (!Array.isArray(u.packages)) return what + " (the list of updated packages could not be read)";
  const n = updatedCount(u);
  if (n === 0) return what + ": rebuilt with fixes, no package versions change";
  return what + ": " + count(n, "package", "packages") + " updated, including " +
    u.packages.slice(0, shown).map(p => label(p) + " " + p.before + " → " + p.after).join(", ");
}

function day(seconds) { return Number.isInteger(seconds) ? new Date(seconds * 1000).toISOString().slice(0, 10) : "?" }

// A NexusOS update: the release branch this computer follows moved forward.
export function isNexusUpdate(u) {
  return !!u && !!u.nexusos && typeof u.nexusos === "object" && !!u.nexusos.before && !!u.nexusos.after;
}

// "NixOS nixos-26.05: 7993f34 (2026-10-12) → 0a1b2c3 (2026-10-28)", after "NexusOS: 1a2b3c4 (…) → 5d6e7f8 (…); " for a NexusOS update.
export function revisions(u) {
  const n = u && u.nixpkgs;
  if (!n || !n.before || !n.after) return "";
  const side = s => String(s.rev).slice(0, 7) + " (" + day(s.lastModified) + ")";
  const nexus = isNexusUpdate(u) ? "NexusOS: " + side(u.nexusos.before) + " → " + side(u.nexusos.after) + "; " : "";
  return nexus + "NixOS " + n.branch + ": " + side(n.before) + " → " + side(n.after);
}

export function shortId(id) { return typeof id === "string" ? id.slice(0, 8) : "" }

function count(n, one, many) { return n + " " + (n === 1 ? one : many) }

// "Approve system change 1a2b3c4d: 2 settings, 1 package"
export function title(r) {
  if (!isRecord(r)) return "";
  if (r.kind === "update") {
    if (!isUpdate(r)) return "";
    const n = updatedCount(r.update);
    return "Approve " + (isNexusUpdate(r.update) ? "the NexusOS update " : "security updates ") + shortId(r.candidateId) + ": " + (n ? count(n, "package", "packages") + " updated" : "rebuilt with fixes");
  }
  const parts = [];
  if (r.settings.length) parts.push(count(r.settings.length, "setting", "settings"));
  if (r.packages.length) parts.push(count(r.packages.length, "package", "packages"));
  return "Approve system change " + shortId(r.candidateId) + ": " + parts.join(", ");
}

// The record's items in the dialog's format ({ kind, name, label, before, after }).
export function items(r) {
  if (!isRecord(r)) return [];
  if (r.kind === "update")
    return isUpdate(r) && Array.isArray(r.update.packages)
      ? r.update.packages.map(p => ({ kind: "update", name: p.name, label: label(p), before: p.before, after: p.after })) : [];
  return r.settings.map(s => ({ kind: "setting", name: s.option, label: s.summary || s.option, before: s.before, after: s.after }))
    .concat(r.packages.map(p => ({ kind: "package", name: p.package, label: p.package, before: p.before, after: true })));
}

function key(item) { return item.kind + ":" + item.name + "=" + (item.kind === "package" ? "true" : String(item.after)) }

// Whether Core's summary (its proposal's items) names exactly the broker's changes;
// for an update, also the same lock (coreLock, the proposal's lockHash).
export function agrees(coreItems, r, coreLock) {
  if (!isRecord(r) || !Array.isArray(coreItems)) return false;
  if (r.kind === "update" && (!isUpdate(r) || coreLock !== r.update.lockHash)) return false;
  const mine = coreItems.map(key).sort(), theirs = items(r).map(key).sort();
  return mine.length === theirs.length && mine.every((k, i) => k === theirs[i]);
}

// The helper's arguments: what polkit's prompt names ($(command_line)) and what
// the root helper checks against the broker after the password.
export function helperArgs(r) { return isRecord(r) ? [r.candidateId, r.buildHash] : [] }

// Whether a polkit request is this dialog's approval of this record: Guardian's
// approve action, and its message (polkit fills in the helper's command line)
// names exactly this candidate and build and no other. Anything else gets the
// plain password window, which shows the action and message as they are.
export function ownsFlow(actionId, message, r) {
  if (actionId !== APPROVE_ACTION || !isRecord(r) || typeof message !== "string") return false;
  const words = message.split(/[^0-9a-f]+/);
  const ids = words.filter(w => w.length === 24), builds = words.filter(w => w.length === 64);
  return ids.length === 1 && ids[0] === r.candidateId && builds.length === 1 && builds[0] === r.buildHash;
}
