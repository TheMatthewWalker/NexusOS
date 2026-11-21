// What the notification cards and centre decide (Notifications.qml,
// NotificationCard.qml). Plain JavaScript, tested with Node (tests/shell/notifications.test.mjs).

export const MAX_CARDS = 3;
export const DEFAULT_SECONDS = 6;

// A card for a new notification: always for a critical one; otherwise not
// while Do not disturb is on or the centre is open (it is in the centre already).
export function showCard(critical, doNotDisturb, centreOpen) {
  if (critical) return true;
  return !doNotDisturb && !centreOpen;
}

// How long a card stays, in milliseconds: the app's timeout when it gives a
// reasonable one (seconds, as Quickshell reports it), else 6 seconds.
export function cardTimeout(expireTimeout) {
  const s = Number(expireTimeout);
  if (!Number.isFinite(s) || s <= 0) return DEFAULT_SECONDS * 1000;
  return Math.round(Math.min(Math.max(s, 2), 30) * 1000);
}

// The action a click on the card runs: the one the app marks "default".
export function defaultAction(actions) {
  return (actions || []).find(a => a && a.identifier === "default") || null;
}

// The buttons: the app's other actions, at most three, each with a label.
export function buttons(actions) {
  return (actions || []).filter(a => a && a.identifier !== "default" && String(a.text || "").trim() !== "").slice(0, 3);
}

// The body as plain text: apps may send simple markup (<b>, &amp;) even when
// told not to; it is shown without the tags, never interpreted.
export function plain(body) {
  return String(body || "")
    .replace(/<[^>]*>/g, "")
    .replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, "&")
    .trim();
}

export function appName(name) { return String(name || "").trim() || "An app" }

// An app icon given as a file path or URL; "" for a theme icon name
// ("mail-unread"), which the card looks up with Quickshell.iconPath.
export function iconSource(icon) {
  const s = String(icon || "");
  if (s.startsWith("/")) return "file://" + s;
  if (/^[a-z]+:\/\//.test(s)) return s;
  return "";
}
