import assert from "node:assert/strict";
import { test } from "node:test";
import * as S from "./switcher.mjs";

const clients = [
  { address: "0xa", title: "Report", class: "libreoffice", focusHistoryID: 2, workspace: { id: 1 }, mapped: true },
  { address: "0xb", title: "Mail", class: "thunderbird", focusHistoryID: 0, workspace: { id: 1 }, mapped: true },
  { address: "0xc", title: "Scratch", class: "kitty", focusHistoryID: 1, workspace: { id: -98 }, mapped: true },
  { address: "0xd", title: "Music", class: "spotify", focusHistoryID: 1, workspace: { id: 2 }, mapped: true },
  { address: "0xe", title: "", class: "hidden", focusHistoryID: 3, workspace: { id: 1 }, hidden: true }
];

test("windows come most recent first, without special workspaces or hidden ones", () => {
  assert.deepEqual(S.order(clients).map(w => w.address), ["0xb", "0xd", "0xa"]);
  assert.equal(S.order([]).length, 0);
});

test("the first press goes to the window before, then steps round", () => {
  assert.equal(S.first(3, 1), 1);
  assert.equal(S.first(3, -1), 2);
  assert.equal(S.first(1, 1), 0);
  assert.equal(S.step(2, 3, 1), 0);
  assert.equal(S.step(0, 3, -1), 2);
  assert.equal(S.step(0, 0, 1), -1);
});
