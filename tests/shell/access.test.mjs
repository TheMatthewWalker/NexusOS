import assert from "node:assert/strict";
import { test } from "node:test";
import * as A from "./access.mjs";

test("zoom steps through the levels and stops at the ends", () => {
  assert.equal(A.zoomStep(1, 1), 1.5);
  assert.equal(A.zoomStep(2, 1), 3);
  assert.equal(A.zoomStep(4, 1), 4);
  assert.equal(A.zoomStep(1.5, -1), 1);
  assert.equal(A.zoomStep(1, -1), 1);
  assert.equal(A.zoomStep(2.5, -1), 2);
});

test("only known filters become a shader path", () => {
  assert.equal(A.shader("protanopia", "/s"), "/s/filters/protanopia.frag");
  assert.equal(A.shader("none", "/s"), "[[EMPTY]]");
  assert.equal(A.shader("../../etc/x", "/s"), "[[EMPTY]]");
  assert.equal(A.filterLabel("invert"), "Inverted");
});
