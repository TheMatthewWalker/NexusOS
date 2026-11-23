import { test } from "node:test";
import assert from "node:assert/strict";
import * as S from "./storage.mjs";

test("the disk, in words", () => {
  const v = { totalBytes: 512e9, freeBytes: 128e9 };
  assert.equal(S.diskLine(v), "128 GB free of 512 GB (75% used)");
  assert.equal(S.low(v), false);
  assert.equal(S.low({ totalBytes: 512e9, freeBytes: 8e9 }), true);
  assert.equal(S.low({ totalBytes: 2e12, freeBytes: 150e9 }), true);
  assert.equal(S.sizeLabel(1.5e9), "1.5 GB");
  assert.equal(S.sizeLabel(0), "nothing");
});

test("kinds as shares, biggest first; results", () => {
  const s = S.shares([{ label: "Music", bytes: 1e9 }, { label: "Videos", bytes: 3e9 }], 10e9);
  assert.deepEqual(s.map(k => k.label), ["Videos", "Music"]);
  assert.equal(s[0].share, 0.3);
  assert.equal(S.freed({ freedBytes: 2e9 }), " 2.0 GB freed.");
  assert.equal(S.freed({}), "");
  assert.deepEqual(S.parse('{"kinds": []}'), { kinds: [] });
});
