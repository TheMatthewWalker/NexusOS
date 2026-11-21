import { test } from "node:test";
import assert from "node:assert/strict";
import * as S from "./secureboot.mjs";

test("each step says what to do next", () => {
  assert.equal(S.step(S.parse('{"step": "prepare"}')).action, "propose");
  assert.equal(S.step({ step: "setup" }).action, "firmware");
  assert.equal(S.step({ step: "enroll" }).action, "enroll");
  assert.equal(S.step({ step: "turn-on" }).action, "firmware");
  assert.equal(S.step({ step: "on" }).action, "");
  assert.match(S.step({ step: "unsupported" }).detail, /UEFI/);
  assert.equal(S.parse("nonsense"), null);
  assert.deepEqual(S.PLAN.changes, [{ option: "nexusos.secureBoot.enable", enabled: true }]);
});
