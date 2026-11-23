import { test } from "node:test";
import assert from "node:assert/strict";
import * as P from "./pickers.mjs";

test("clipboard history from cliphist", () => {
  const c = P.clips("12\thttps://example.com\n11\t[[ binary data 52 KiB png 1920x1080 ]]\nnonsense\n10\tline\twith tab\n");
  assert.deepEqual(c.map(x => x.id), ["12", "11", "10"]);
  assert.equal(c[1].preview, "Picture · 1920x1080");
  assert.equal(c[1].image, true);
  assert.equal(c[2].preview, "line\twith tab");
  assert.deepEqual(P.searchClips(c, "EXAMPLE").map(x => x.id), ["12"]);
});

test("emoji from Unicode's list, searched by name and group", () => {
  const text = [
    "# group: Smileys & Emotion",
    "1F600                                                  ; fully-qualified     # 😀 E1.0 grinning face",
    "263A FE0F                                              ; fully-qualified     # ☺️ E0.6 smiling face",
    "263A                                                   ; unqualified         # ☺ E0.6 smiling face",
    "# group: Component",
    "1F3FB                                                  ; fully-qualified     # 🏻 E1.0 light skin tone",
    "# group: Food & Drink",
    "1F355                                                  ; fully-qualified     # 🍕 E0.6 pizza"
  ].join("\n");
  const e = P.emoji(text);
  assert.deepEqual(e.map(x => x.char), ["😀", "☺️", "🍕"]);
  assert.deepEqual(P.searchEmoji(e, "face").map(x => x.name), ["grinning face", "smiling face"]);
  assert.deepEqual(P.searchEmoji(e, "", "Food & Drink").map(x => x.char), ["🍕"]);
  assert.deepEqual(P.groups(e), ["Smileys & Emotion", "Food & Drink"]);
});
