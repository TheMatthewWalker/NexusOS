// Settings, Help (HelpPage.qml): the user guide (docs/USER-GUIDE.md) as topics.
// Tested with Node (tests/shell/help.test.mjs).

// "## Title" starts a topic; what comes before the first one is the introduction.
export function topics(markdown) {
  const out = [];
  let current = { title: "About this guide", body: [] };
  for (const line of String(markdown || "").split("\n")) {
    if (/^# /.test(line)) continue;
    const m = /^## (.+)$/.exec(line);
    if (m) { if (current.body.join("").trim()) out.push(current); current = { title: m[1].trim(), body: [] }; continue }
    current.body.push(line);
  }
  if (current.body.join("").trim()) out.push(current);
  return out.map(t => ({ title: t.title, body: t.body.join("\n").trim() }));
}

// Topics whose title or text has every word of the query, titles first.
export function search(list, query) {
  const words = String(query || "").toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return list;
  const has = (t, field) => words.every(w => t[field].toLowerCase().includes(w));
  return list.filter(t => has(t, "title")).concat(list.filter(t => !has(t, "title") && (has(t, "body") || has({ x: t.title + " " + t.body }, "x"))));
}

// A link in the guide: settings:<page>/<highlight> opens Settings; https links open the browser; nothing else.
export function link(href, pages) {
  const m = /^settings:([a-z]+)\/([a-z]{0,20})$/.exec(String(href || ""));
  if (m) return (pages || []).includes(m[1]) ? { page: m[1], highlight: m[2] } : null;
  return /^https:\/\/[^\s"'<>]+$/.test(String(href || "")) ? { url: href } : null;
}
