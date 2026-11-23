// Alt+Tab (Switcher.qml): the open windows, the one used last first, as on
// Windows. Tested with Node (tests/shell/switcher.test.mjs).

// `hyprctl clients -j`: windows on normal workspaces, most recently focused
// first (focusHistoryID 0 is the one in use now).
export function order(clients) {
  return (clients || [])
    .filter(c => c && c.mapped !== false && c.hidden !== true && c.address && c.workspace && c.workspace.id > 0)
    .slice()
    .sort((a, b) => (a.focusHistoryID ?? 1e9) - (b.focusHistoryID ?? 1e9))
    .map(c => ({ address: c.address, title: c.title || c.class || "Window", appId: c.class || c.initialClass || "",
                 workspace: c.workspace.id }));
}

// The first press picks the window before this one; each Tab moves on,
// Shift+Tab back, wrapping round.
export function step(index, count, direction) {
  if (count <= 0) return -1;
  return ((index + (direction < 0 ? -1 : 1)) % count + count) % count;
}
export function first(count, direction) { return count <= 1 ? 0 : (direction < 0 ? count - 1 : 1) }
