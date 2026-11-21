import { test } from "node:test";
import assert from "node:assert/strict";
import * as P from "./printers.mjs";

test("lpstat output is read: destinations, the default and each state", () => {
  assert.deepEqual(P.destinations("Office\nHP_LaserJet_M110w\n\nOffice\n"), ["Office", "HP_LaserJet_M110w"]);
  assert.deepEqual(P.destinations("lpstat: No destinations added."), []);
  assert.equal(P.defaultDestination("system default destination: Office"), "Office");
  assert.equal(P.defaultDestination("no system default destination"), "");
  const s = P.states("printer Office is idle.  enabled since Thu 19 Nov 2026\nprinter HP now printing HP-12.  enabled since x\nprinter Old disabled since y -\n\treason unknown");
  assert.deepEqual(s, { Office: "idle", HP: "printing", Old: "paused" });
  assert.equal(P.stateLabel("paused"), "Paused");
});

test("names read as people say them, and only safe names are passed on", () => {
  assert.equal(P.displayName("HP_LaserJet_M110w"), "HP LaserJet M110w");
  assert.equal(P.validName("HP_LaserJet-M110w"), true);
  assert.equal(P.validName("-o evil"), false);
  assert.equal(P.validName("a b"), false);
  assert.match(P.testPage("HP_LaserJet", "today"), /Printer: HP LaserJet/);
});
