// Settings, Language and region (RegionPage.qml) and the keyboard layouts
// DesktopSettings.qml applies. Tested with Node (tests/shell/region.test.mjs).
//
// ~/.config/nexusos/region.env is read by the session launcher at login
// (nix/modules/desktop.nix), which accepts only these names and characters.

export const SWITCH = "grp:alt_shift_toggle";
const LOCALE = /^[a-z]{2,3}_[A-Z]{2}\.UTF-8(@[a-z]+)?$/;
const ZONE = /^(UTC|[A-Z][A-Za-z_]+(\/[A-Z][A-Za-z0-9_+-]+){1,2})$/;
const LAYOUT = /^[a-z]{2,3}$/;

export function validLocale(l) { return LOCALE.test(String(l || "")) }
export function validZone(z) { return ZONE.test(String(z || "")) && !String(z).includes("..") }

// "fr_FR.UTF-8" -> "fr"
export function languageCode(locale) { const m = /^([a-z]{2,3})_/.exec(String(locale || "")); return m ? m[1] : "en" }

export function parseEnv(text) {
  const out = {};
  for (const line of String(text || "").split("\n")) {
    const m = /^([A-Z_]+)=([A-Za-z0-9_.@:\/+-]+)$/.exec(line.trim());
    if (m) out[m[1]] = m[2];
  }
  return { language: out.LANG || "", formats: out.LC_TIME || "", timeZone: out.TZ || "",
           input: ["fcitx5", "none"].includes(out.NEXUS_INPUT) ? out.NEXUS_INPUT : "" };
}

// What the person chose -> region.env. Anything not valid is left out (the system's own is used).
export function envFile(choice) {
  const lines = [];
  if (validLocale(choice.language)) {
    lines.push("LANG=" + choice.language);
    const code = languageCode(choice.language);
    lines.push("LANGUAGE=" + choice.language.replace(".UTF-8", "") + ":" + code + (code === "en" ? "" : ":en"));
  }
  if (validLocale(choice.formats))
    for (const k of ["LC_TIME", "LC_NUMERIC", "LC_MONETARY", "LC_PAPER", "LC_MEASUREMENT"]) lines.push(k + "=" + choice.formats);
  if (validZone(choice.timeZone)) lines.push("TZ=" + choice.timeZone);
  // Chinese, Japanese and Korean typing (fcitx5): "" is automatic (on for those languages).
  if (choice.input === "fcitx5" || choice.input === "none") lines.push("NEXUS_INPUT=" + choice.input);
  return lines.join("\n") + (lines.length ? "\n" : "");
}

// Extra layouts as stored ("fr,de") -> Hyprland's input:kb_layout and kb_options, or null for the base layout alone.
export function keyboard(base, extras) {
  const list = [base].concat(String(extras || "").split(",")).filter(l => LAYOUT.test(l));
  const unique = [...new Set(list)];
  if (unique.length < 2) return null;
  return { layout: unique.slice(0, 4).join(","), options: SWITCH };
}

export function addLayout(extras, layout, base) {
  const list = String(extras || "").split(",").filter(Boolean);
  if (!LAYOUT.test(layout) || layout === base || list.includes(layout) || list.length >= 3) return extras || "";
  return list.concat([layout]).join(",");
}
export function removeLayout(extras, layout) { return String(extras || "").split(",").filter(l => l && l !== layout).join(",") }

// timedatectl list-timezones output, searched.
export function zones(text, query) {
  const q = String(query || "").toLowerCase().replace(/ /g, "_");
  return String(text || "").split("\n").map(z => z.trim()).filter(z => validZone(z))
    .filter(z => !q || z.toLowerCase().includes(q)).slice(0, 8);
}
export function zoneLabel(z) { return String(z || "").replace(/_/g, " ").replace(/\//g, " / ") }

export function localeName(list, locale) {
  const f = (list || []).find(l => l.locale === locale);
  return f ? f.name : (locale || "").replace(".UTF-8", "");
}

// Whether the input method is on at the next login (the session launcher's rule).
export function inputMethodOn(choice) {
  if (choice.input === "fcitx5") return true;
  if (choice.input === "none") return false;
  return /^(zh|ja|ko)_/.test(String(choice.language || ""));
}
