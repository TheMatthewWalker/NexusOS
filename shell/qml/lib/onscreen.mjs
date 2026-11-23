// The volume, microphone and brightness keys (OnScreen.qml). Tested with Node
// (tests/shell/onscreen.test.mjs).

export const STEP = 0.05;

// One press up or down, kept between 0 and 100% (never past 100: louder
// distorts) and on whole steps, as Windows does.
export function step(value, up) {
  const v = Math.round((Number(value) || 0) / STEP) * STEP + (up ? STEP : -STEP);
  return Math.round(Math.min(1, Math.max(0, v)) * 100) / 100;
}

// The screen is never made fully black by a key: it must stay readable.
export function brightnessStep(value, up) { return Math.max(0.05, step(value, up)) }

// brightnessctl -m: "intel_backlight,backlight,4800,50%,9600" -> { device, value 0..1 }
export function parseBrightness(text) {
  const f = String(text || "").trim().split("\n")[0].split(",");
  if (f.length < 5) return null;
  const pct = parseInt(f[3]);
  return isNaN(pct) ? null : { device: f[0], value: pct / 100 };
}

export function label(kind, value, muted) {
  if (kind === "volume") return muted ? "Muted" : Math.round(value * 100) + "%";
  if (kind === "microphone") return muted ? "Microphone off" : "Microphone on";
  if (kind === "capslock") return value ? "Caps Lock on" : "Caps Lock off";
  return Math.round(value * 100) + "%";
}

export function icon(kind, value, muted) {
  if (kind === "brightness") return "display";
  if (kind === "microphone") return "mic";
  if (kind === "capslock") return "language";
  return "sound";
}

// Kinds shown as a bar of how much (not microphone or Caps Lock, which are on or off).
export function hasBar(kind) { return kind === "volume" || kind === "brightness" }

// `hyprctl devices -j`: Caps Lock is on if the main keyboard (or, with none
// marked main, any keyboard) says so.
export function capsLock(text) {
  let d;
  try { d = JSON.parse(text) } catch (e) { return null }
  const kbs = (d && d.keyboards) || [];
  if (kbs.length === 0) return null;
  const main = kbs.filter(k => k.main);
  return (main.length ? main : kbs).some(k => k.capsLock === true);
}
