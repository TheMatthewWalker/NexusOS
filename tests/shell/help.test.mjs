import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import * as H from "./help.mjs";

const guide = "# Guide\n\nIntro text.\n\n## Backups\n\nPlug in a drive. [Open Backups](settings:backups/backups)\n\n## Printers\n\nThey appear by themselves.\n";

test("the guide becomes topics", () => {
  const t = H.topics(guide);
  assert.deepEqual(t.map(x => x.title), ["About this guide", "Backups", "Printers"]);
  assert.equal(t[2].body, "They appear by themselves.");
});

test("searching finds titles first, then text", () => {
  const t = H.topics(guide);
  assert.deepEqual(H.search(t, "printers").map(x => x.title), ["Printers"]);
  assert.deepEqual(H.search(t, "drive").map(x => x.title), ["Backups"]);
  assert.equal(H.search(t, "").length, 3);
  assert.deepEqual(H.search(t, "nothing here"), []);
});

test("links open Settings pages or the web, and nothing else", () => {
  const pages = ["backups", "about"];
  assert.deepEqual(H.link("settings:backups/backups", pages), { page: "backups", highlight: "backups" });
  assert.deepEqual(H.link("settings:about/", pages), { page: "about", highlight: "" });
  assert.equal(H.link("settings:evil/x", pages), null);
  assert.deepEqual(H.link("https://github.com/x", pages), { url: "https://github.com/x" });
  assert.equal(H.link("file:///etc/shadow", pages), null);
  assert.equal(H.link("javascript:alert(1)", pages), null);
});

test("every Settings link in the real guide names a page and an item Settings has", () => {
  const md = readFileSync(new URL("../help/user-guide.md", import.meta.url), "utf8");
  const settings = readFileSync(new URL("../Settings.qml", import.meta.url), "utf8");
  const pages = [...settings.matchAll(/\{ id: "([a-z]+)", label:/g)].map(m => m[1]);
  const items = new Set([...settings.matchAll(/\["([a-z]+)", "([a-z]*)", "/g)].map(m => m[1] + "/" + m[2]));
  const links = [...md.matchAll(/\]\((settings:[^)]+)\)/g)].map(m => m[1]);
  assert.ok(links.length > 10);
  for (const l of links) {
    assert.ok(H.link(l, pages), l + " is not a Settings page");
    assert.ok(items.has(l.slice("settings:".length)), l + " is not an item in Settings' search index");
  }
});
