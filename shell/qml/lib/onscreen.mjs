// The volume, microphone, brightness and keyboard light keys (OnScreen.qml). Tested with Node
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

// The keyboard's backlight (`brightnessctl -m -l -c leds`): the first LED
// called *kbd_backlight*, as { device, level, max }, or null for none.
export function parseKeyboardLight(text) {
  for (const line of String(text || "").split("\n")) {
    const f = line.trim().split(",");
    if (f.length < 5 || !/kbd_backlight/.test(f[0])) continue;
    const level = parseInt(f[2]), max = parseInt(f[4]);
    if (!isNaN(level) && max > 0) return { device: f[0], level, max };
  }
  return null;
}
// Keyboard lights have a few levels (often off, low, high): up and down
// stop at the ends; the light key itself ("cycle") goes round, as on Windows laptops.
export function keyboardStep(light, how) {
  if (how === "cycle") return light.level >= light.max ? 0 : light.level + 1;
  return Math.min(light.max, Math.max(0, light.level + (how === "up" ? 1 : -1)));
}

export function label(kind, value, muted) {
  if (kind === "keyboard") return value > 0 ? "Keyboard light " + Math.round(value * 100) + "%" : "Keyboard light off";
  if (kind === "volume") return muted ? "Muted" : Math.round(value * 100) + "%";
  if (kind === "microphone") return muted ? "Microphone off" : "Microphone on";
  if (kind === "capslock") return value ? "Caps Lock on" : "Caps Lock off";
  return Math.round(value * 100) + "%";
}

export function icon(kind, value, muted) {
  if (kind === "brightness") return "display";
  if (kind === "microphone") return "mic";
  if (kind === "capslock" || kind === "keyboard") return "language";
  return "sound";
}

// Kinds shown as a bar of how much (not microphone or Caps Lock, which are on or off).
export function hasBar(kind) { return kind === "volume" || kind === "brightness" || kind === "keyboard" }

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
