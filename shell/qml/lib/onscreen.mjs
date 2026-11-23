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
  return Math.round(value * 100) + "%";
}

export function icon(kind, value, muted) {
  if (kind === "brightness") return "display";
  if (kind === "microphone") return "mic";
  return "sound";
}
