// Settings, Accessibility: the magnifier and colour filters (DesktopSettings.qml
// applies them to Hyprland). Tested with Node (tests/shell/access.test.mjs).

// Magnifier levels; Super+Plus and Super+Minus step through them.
export const zoomLevels = [1, 1.5, 2, 3, 4];
export function zoomStep(current, direction) {
  const i = zoomLevels.findIndex(z => z >= current - 1e-6);
  const at = i < 0 ? zoomLevels.length - 1 : i;
  return zoomLevels[Math.max(0, Math.min(zoomLevels.length - 1, at + (direction > 0 ? 1 : -1)))];
}

export const filters = [
  { value: "none", short: "None", label: "None" },
  { value: "greyscale", short: "Grey", label: "Greyscale" },
  { value: "invert", short: "Inverted", label: "Inverted" },
  { value: "protanopia", short: "Red", label: "Red-blind (protanopia)" },
  { value: "deuteranopia", short: "Green", label: "Green-blind (deuteranopia)" },
  { value: "tritanopia", short: "Blue", label: "Blue-blind (tritanopia)" }
];

// Hyprland's decoration:screen_shader for a filter; "[[EMPTY]]" turns it off.
export function shader(filter, shellDir) {
  return filters.some(f => f.value === filter && f.value !== "none") ? shellDir + "/filters/" + filter + ".frag" : "[[EMPTY]]";
}
export function filterLabel(filter) { const f = filters.find(x => x.value === filter); return f ? f.label : "None" }
