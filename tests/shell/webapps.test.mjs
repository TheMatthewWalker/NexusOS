import assert from "node:assert/strict";
import { test } from "node:test";
import * as W from "./webapps.mjs";

test("addresses are completed and unsafe ones refused", () => {
  assert.equal(W.normalise("outlook.office.com"), "https://outlook.office.com/");
  assert.equal(W.normalise("https://example.com/a?b=1"), "https://example.com/a?b=1");
  for (const bad of ["", "javascript:alert(1)", "file:///etc/passwd", "https://a.com/'x'", "https://a.com/ b", "https://a.com/$(id)"])
    assert.equal(W.normalise(bad), "", bad);
});

test("names come from the address", () => {
  assert.equal(W.nameFor("https://www.youtube.com/"), "Youtube");
  assert.equal(W.nameFor("https://docs.google.com/x"), "Docs");
});

test("presets are all valid and known when added", () => {
  for (const p of W.presets) assert.equal(W.normalise(p.url), p.url, p.name);
  assert.ok(W.added([{ url: W.presets[0].url }], W.presets[0].url));
  assert.ok(!W.added([], W.presets[0].url));
});
