// Settings, Display, Screens: arranging more than one screen. The person's
// choices are kept in desktop.json as monitorLayout (DesktopSettings.qml) and
// turned into Hyprland monitor rules here, so they come back at every login
// and whenever the screen is plugged in again.
// Tested with Node (tests/shell/displays.test.mjs).
//
// monitorLayout: { "<screen key>": { place, mode, scale } }
//   key    the screen's make, model and serial (stable across ports and docks),
//          else its connector name
//   place  "main", "right", "left", "above", "below" (of the main screen),
//          "mirror" (shows the main screen) or "off"
//   mode   "2560x1440@59.95" (or "" for the screen's preferred mode)
//   scale  1 to 3

export const PLACES = ["main", "left", "right", "above", "below", "mirror", "off"];
export const SCALES = [1, 1.25, 1.5, 1.75, 2];
const SAFE = /^[^,;#\n\r]{1,120}$/;
const MODE = /^\d{3,5}x\d{3,5}(@\d{2,3}(\.\d{1,3})?)?$/;

function description(m) { return String(m.description || "").replace(/\s*\([A-Za-z0-9-]+\)\s*$/, "").trim() }
export function key(m) { const d = description(m); return d && SAFE.test(d) ? d : String(m.name || "") }
function selector(m) { const d = description(m); return d && SAFE.test(d) && d === key(m) ? "desc:" + d : m.name }

// hyprctl monitors all -j
export function parse(text) {
  let list;
  try { list = JSON.parse(String(text || "")) } catch (e) { return [] }
  if (!Array.isArray(list)) return [];
  return list.filter(m => m && SAFE.test(String(m.name || "")) && /^[A-Za-z0-9-]+$/.test(m.name)).map(m => ({
    name: m.name, key: key(m), selector: selector(m), label: [m.make, m.model].filter(Boolean).join(" ") || m.name,
    builtIn: /^(eDP|LVDS|DSI)/.test(m.name), width: m.width, height: m.height, refresh: m.refreshRate,
    scale: m.scale, disabled: !!m.disabled, modes: modes(m.availableModes || [])
  }));
}

// Each size once, at its highest refresh rate, largest first; at most 6.
export function modes(available) {
  const best = {};
  for (const raw of available) {
    const m = /^(\d+)x(\d+)@([\d.]+)Hz$/.exec(String(raw));
    if (!m) continue;
    const size = m[1] + "x" + m[2], rate = parseFloat(m[3]);
    if (!best[size] || rate > best[size].rate) best[size] = { size, rate, w: +m[1], h: +m[2] };
  }
  return Object.values(best).sort((a, b) => b.w * b.h - a.w * a.h).slice(0, 6)
    .map(b => ({ value: b.size + "@" + b.rate.toFixed(2).replace(/\.?0+$/, ""), label: b.size.replace("x", " × ") + (b.rate >= 70 ? " · " + Math.round(b.rate) + " Hz" : "") }));
}

export function placeLabel(p) {
  return ({ main: "Main", left: "Left", right: "Right", above: "Above", below: "Below", mirror: "Same picture", off: "Off" })[p] || p;
}

function setting(layout, m) {
  const s = (layout || {})[m.key] || {};
  return {
    place: PLACES.includes(s.place) ? s.place : "",
    mode: MODE.test(String(s.mode || "")) ? s.mode : "",
    scale: typeof s.scale === "number" && s.scale >= 0.5 && s.scale <= 3 ? s.scale : 0
  };
}

// The main screen: the one chosen, else the built-in one, else the first.
export function main(monitors, layout) {
  const chosen = monitors.find(m => setting(layout, m).place === "main");
  return chosen || monitors.find(m => m.builtIn && setting(layout, m).place !== "off") ||
    monitors.find(m => setting(layout, m).place !== "off") || monitors[0];
}

function size(m, s, fallbackScale) {
  const mm = /^(\d+)x(\d+)/.exec(s.mode || "");
  const w = mm ? +mm[1] : m.width, h = mm ? +mm[2] : m.height;
  const scale = s.scale || fallbackScale || 1;
  return { w: Math.round(w / scale), h: Math.round(h / scale), scale };
}

// The Hyprland keywords for the screens connected now: "monitor SEL,MODE,XxY,SCALE".
export function rules(monitors, layout, defaultScale) {
  if (!monitors.length) return [];
  const first = main(monitors, layout);
  const placed = [], out = [];
  let right = 0, left = 0, top = 0, bottom = 0;
  const mainSize = size(first, setting(layout, first), defaultScale);
  right = mainSize.w; bottom = mainSize.h;
  placed.push({ m: first, s: setting(layout, first), size: mainSize, x: 0, y: 0 });
  for (const m of monitors) {
    if (m.key === first.key) continue;
    const s = setting(layout, m), sz = size(m, s, defaultScale);
    if (s.place === "off") { out.push("monitor " + m.selector + ",disable"); continue }
    if (s.place === "mirror") {
      out.push("monitor " + m.selector + "," + (s.mode || "preferred") + ",auto," + sz.scale + ",mirror," + first.name);
      continue;
    }
    const place = s.place && s.place !== "main" ? s.place : "right";
    let x = 0, y = 0;
    if (place === "right") { x = right; right += sz.w }
    else if (place === "left") { left -= sz.w; x = left }
    else if (place === "below") { y = bottom; bottom += sz.h }
    else { top -= sz.h; y = top }
    placed.push({ m, s, size: sz, x, y });
  }
  // Hyprland prefers positions from 0: move everything right and down.
  const dx = -Math.min(...placed.map(p => p.x)), dy = -Math.min(...placed.map(p => p.y));
  return placed.map(p => "monitor " + p.m.selector + "," + (p.s.mode || "preferred") + "," + (p.x + dx) + "x" + (p.y + dy) + "," + p.size.scale)
    .concat(out);
}

// (Screens are compared by key: QML hands copies of them around.)
// A change to one screen; "main" moves the old main screen to the right, and
// turning off the last screen that is on is refused.
export function change(layout, monitors, m, field, value) {
  const next = JSON.parse(JSON.stringify(layout || {}));
  if (field === "place") {
    if (!PLACES.includes(value)) return layout || {};
    const on = monitors.filter(o => o.key !== m.key && setting(next, o).place !== "off" && setting(next, o).place !== "mirror");
    if ((value === "off" || value === "mirror") && on.length === 0) return layout || {};
    if (value === "main") {
      const old = main(monitors, next);
      for (const o of monitors) if (setting(next, o).place === "main") next[o.key].place = "right";
      if (old && old.key !== m.key) next[old.key] = Object.assign({}, next[old.key], { place: "right" });
    }
    if ((value === "off" || value === "mirror") && main(monitors, next).key === m.key) {
      const other = on[0];
      next[other.key] = Object.assign({}, next[other.key], { place: "main" });
    }
  }
  if (field === "mode" && !MODE.test(String(value))) return layout || {};
  if (field === "scale" && !SCALES.includes(value)) return layout || {};
  next[m.key] = Object.assign({}, next[m.key], { [field]: value });
  return next;
}

export function current(layout, m, field) { return setting(layout, m)[field] }
