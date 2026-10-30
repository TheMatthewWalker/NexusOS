// What the Guardian dialog shows at approval, and which polkit requests it
// answers. Plain JavaScript so it runs in QML and is tested with Node
// (tests/shell/guardian.test.mjs). Guardian.qml supplies the data.
//
// The record is the broker's own description of a checked candidate
// (GET /guardian/proposals/{id}/describe, the broker's "describe"):
//   { candidateId, buildHash, settings: [{ option, summary, before, after }],
//     packages: [{ package, before }], risk: { level, notes }, staged }
// It, not Core's proposal text, is what the person approves; Core's own
// summary is only shown as "Nexus says".

export const APPROVE_ACTION = "org.nexusos.guardian.approve";

export function isRecord(r) {
  return !!r && typeof r === "object" && /^[0-9a-f]{24}$/.test(r.candidateId || "") &&
    /^[0-9a-f]{64}$/.test(r.buildHash || "") && Array.isArray(r.settings) && Array.isArray(r.packages);
}

export function shortId(id) { return typeof id === "string" ? id.slice(0, 8) : "" }

function count(n, one, many) { return n + " " + (n === 1 ? one : many) }

// "Approve system change 1a2b3c4d: 2 settings, 1 package"
export function title(r) {
  if (!isRecord(r)) return "";
  const parts = [];
  if (r.settings.length) parts.push(count(r.settings.length, "setting", "settings"));
  if (r.packages.length) parts.push(count(r.packages.length, "package", "packages"));
  return "Approve system change " + shortId(r.candidateId) + ": " + parts.join(", ");
}

// The record's items in the dialog's format ({ kind, name, label, before, after }).
export function items(r) {
  if (!isRecord(r)) return [];
  return r.settings.map(s => ({ kind: "setting", name: s.option, label: s.summary || s.option, before: s.before, after: s.after }))
    .concat(r.packages.map(p => ({ kind: "package", name: p.package, label: p.package, before: p.before, after: true })));
}

function key(item) { return item.kind + ":" + item.name + "=" + (item.kind === "package" ? "true" : String(item.after)) }

// Whether Core's summary (its proposal's items) names exactly the broker's changes.
export function agrees(coreItems, r) {
  if (!isRecord(r) || !Array.isArray(coreItems)) return false;
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
