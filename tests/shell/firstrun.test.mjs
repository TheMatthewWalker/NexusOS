// Copied next to the built firstrun.mjs and bundles.mjs by tests/shell/test_shell_build.py.
import test from "node:test";
import assert from "node:assert/strict";
import { match, merge, systemPlan } from "./firstrun.mjs";
import { bundles } from "./bundles.mjs";

test("the six chips are the curated bundles, in order", () => {
  assert.deepEqual(bundles.map(b => b.label), ["Studying", "Gaming", "Office", "Coding", "Creative", "Just browsing"]);
});

test("free text picks bundles from their keywords, and says which words", () => {
  const m = match("Uni work, and learning Python on the side", bundles);
  assert.deepEqual(m.ids, ["studying", "coding"]);
  assert.ok(m.words.includes("uni") && m.words.includes("python"));
  assert.deepEqual(match("", bundles).ids, []);
  assert.deepEqual(match("install rm -rf and curl", bundles).ids, []);
});

test("merged items appear once, tagged with their bundle, and removed ones stay out", () => {
  const all = merge(["studying", "browsing"], bundles, []);
  assert.equal(all.settings.filter(s => s.id === "set:nightLight").length, 1);
  assert.equal(all.settings[0].from, "Studying");
  const fewer = merge(["studying"], bundles, ["app:net.ankiweb.Anki"]);
  assert.ok(!fewer.apps.some(a => a.flathub === "net.ankiweb.Anki"));
  assert.ok(fewer.apps.some(a => a.flathub === "org.zotero.Zotero"));
  assert.equal(merge(["studying"], bundles, ["set:nightLight"]).settings.length, 0);
  assert.equal(merge(["studying"], bundles, []).settings[0].key, "nightLight");
});

test("system items become one typed Guardian plan, never free text", () => {
  const items = merge(["coding", "office"], bundles, []).system;
  const plan = systemPlan(items, ["Office", "Coding"]);
  assert.deepEqual(plan.packages, ["git", "python3", "nodejs"]);
  assert.deepEqual(plan.changes, [{ option: "services.printing.enable", enabled: true }]);
  assert.ok(plan.description.length <= 500);
  assert.equal(systemPlan([], []), null);
});
