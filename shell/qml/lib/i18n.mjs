// The shell's translations. A catalogue is shell/qml/i18n/<language>.json:
// { "English text": "translation", ... }; Translations.qml loads the one for
// the person's language (LANGUAGE/LANG) and t() falls back to English for
// anything not translated yet. "{0}" in a text is replaced by args[0].
// Tested with Node (tests/shell/i18n.test.mjs).

export const LANGUAGES = ["de", "es", "fr"];

// LANGUAGE ("fr_CA:fr:en") or LANG ("fr_CA.UTF-8") -> the first language with a catalogue, or "".
export function pick(language, lang, available) {
  const have = available || LANGUAGES;
  const wanted = String(language || "").split(":").concat([String(lang || "")])
    .map(l => l.replace(/\..*$/, "").split("_")[0]).filter(Boolean);
  for (const w of wanted) { if (w === "en" || w === "C") return ""; if (have.includes(w)) return w }
  return "";
}

export function t(catalogue, text, args) {
  const found = catalogue && typeof catalogue[text] === "string" && catalogue[text] ? catalogue[text] : text;
  return (args || []).reduce((s, a, i) => s.split("{" + i + "}").join(String(a)), found);
}
