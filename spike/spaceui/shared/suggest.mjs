// Shared shell logic for the SpaceUI spike: app slots, emergence paths and the
// prototype's keyword app suggestions. Imported unchanged by the QML shell
// (`import "suggest.mjs" as S`) and the web shell, so both run the same code.
import { tokens } from "./tokens.mjs";

export const APP_IDS = ["files", "browser", "terminal", "studio", "models", "settings"];

export const APP_LABELS = {
  files: "Files", browser: "Browser", terminal: "Terminal",
  studio: "Studio", models: "Models", settings: "Settings",
};

// PROTOTYPE ONLY. Keyword matching ported from the design prototype. In the
// real OS, Core returns structured, allowlisted suggestions; the UI must keep
// labelling these as keyword matches until then.
const PATTERNS = {
  files: /\b(file|files|folder|folders|document|documents|pdf|organise|organize|download|downloads|notes|report)\b/i,
  browser: /\b(research|search|web|website|browse|browser|find|learn|sources|article|articles|read)\b/i,
  terminal: /\b(code|coding|develop|developer|debug|python|script|scripts|terminal|command|linux|build|install|git)\b/i,
  studio: /\b(design|create|creative|image|images|draw|art|video|edit|mockup|logo|canvas|studio|presentation)\b/i,
  models: /\b(ai|model|models|local|llm|inference|train|training|generate|generation|agent|agents)\b/i,
  settings: /\b(settings|wifi|wi-fi|network|bluetooth|brightness|volume|sound|display|battery|configure|preferences|theme)\b/i,
};

export function suggestApps(text) {
  if (!text || !text.trim()) return [];
  const found = APP_IDS.filter(id => PATTERNS[id].test(text));
  const wantsFiles = /\b(research|design|create|code|python|debug)\b/i.test(text);
  if (wantsFiles && !found.includes("files")) found.push("files");
  return found;
}

// Fixed slot for each app on the core's orbit, relative to the core centre.
export function slot(id) {
  const o = tokens.baseline.appOrbit;
  const a = (o.angles[id] * Math.PI) / 180;
  return { x: o.radius * Math.cos(a), y: o.radius * Math.sin(a) };
}

// Apps emerge from the core along a curve through a rotated waypoint, as in
// the prototype: keyframes at times [0, 0.45, 1] for position and scale.
export function emergence(id, progress) {
  const m = tokens.baseline.appOrbit.motion;
  const { x, y } = slot(id);
  const bend = { x: x * 0.46 - y * 0.24, y: y * 0.46 + x * 0.24 };
  const k = m.entryTimes[1];
  const p = Math.min(1, Math.max(0, progress));
  const seg = p < k ? p / k : (p - k) / (1 - k);
  const lerp = (a, b) => a + (b - a) * seg;
  const scale = p < k ? lerp(m.initialScale, 0.62) : lerp(0.62, 1);
  return p < k
    ? { x: lerp(0, bend.x), y: lerp(0, bend.y), scale, opacity: Math.min(1, p / k) }
    : { x: lerp(bend.x, x), y: lerp(bend.y, y), scale, opacity: 1 };
}

// CSS-style cubic-bezier easing, so both shells ease identically.
export function cubicBezier(x1, y1, x2, y2) {
  const bx = t => ((1 - 3 * x2 + 3 * x1) * t + (3 * x2 - 6 * x1)) * t * t + 3 * x1 * t;
  const by = t => ((1 - 3 * y2 + 3 * y1) * t + (3 * y2 - 6 * y1)) * t * t + 3 * y1 * t;
  const dx = t => 3 * (1 - 3 * x2 + 3 * x1) * t * t + 2 * (3 * x2 - 6 * x1) * t + 3 * x1;
  return x => {
    if (x <= 0) return 0;
    if (x >= 1) return 1;
    let t = x;
    for (let i = 0; i < 8; i++) {
      const d = dx(t);
      if (Math.abs(d) < 1e-6) break;
      t -= (bx(t) - x) / d;
    }
    let lo = 0, hi = 1;
    for (let i = 0; i < 30 && Math.abs(bx(t) - x) > 1e-6; i++) {
      if (bx(t) < x) lo = t; else hi = t;
      t = (lo + hi) / 2;
    }
    return by(t);
  };
}

export const entryEase = cubicBezier(...tokens.baseline.appOrbit.motion.entryEase);
export const exitEase = cubicBezier(...tokens.baseline.appOrbit.motion.exitEase);

// Tiny planets for open apps around the bottom launcher. The launcher sits at
// the screen edge, so planets use the upper half-circle only.
export function launcherPlanet(index, count, expanded) {
  const radius = expanded ? 104 : 40;
  const span = expanded ? 150 : 120;
  const start = -90 - span / 2;
  const angle = count <= 1 ? -90 : start + (span * index) / (count - 1);
  const a = (angle * Math.PI) / 180;
  return { x: radius * Math.cos(a), y: radius * Math.sin(a), size: expanded ? 46 : 12 };
}

// Simulated response envelope (labelled as simulated in the UI): a smoothed
// sum of three bands, driving the core's gas while a reply is shown.
export function envelope(t) {
  const v = 0.5 + 0.22 * Math.sin(t * 7.1) + 0.16 * Math.sin(t * 3.3 + 1.2) + 0.12 * Math.sin(t * 13.7 + 0.4);
  return Math.min(1, Math.max(0, v));
}
