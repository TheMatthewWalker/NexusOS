import { test } from "node:test";
import assert from "node:assert/strict";
import * as F from "./firmware.mjs";

const id = "a".repeat(40);
const updates = JSON.stringify({ Devices: [
  { Name: "System Firmware", Vendor: "Dell", DeviceId: id, Version: "1.2.0", Flags: ["internal", "updatable", "needs-reboot"],
    Releases: [{ Version: "1.4.1", Summary: "Firmware for the XPS", Description: "<p>Fixes:</p><ul><li>Battery &amp; fan</li></ul>", Urgency: "high" }] },
  { Name: "Mouse", DeviceId: "b".repeat(40), Version: "3", Flags: ["updatable"], Releases: [] },
  { Name: "Odd", DeviceId: "--evil", Releases: [{ Version: "9" }] }
] });

test("updates are the devices with a newer release", () => {
  const u = F.updates(updates);
  assert.equal(u.length, 1);
  assert.equal(u[0].id, id);
  assert.equal(u[0].urgent, true);
  assert.equal(u[0].restart, true);
  assert.equal(u[0].notes, "Fixes:\n• Battery & fan");
  assert.equal(F.line(u[0]), "Dell System Firmware: 1.2.0 → 1.4.1 · important · finishes when you restart");
  assert.deepEqual(F.updates("not json"), []);
});

test("devices, the summary and fwupdmgr's results", () => {
  assert.deepEqual(F.devices(updates).map(d => d.name), ["System Firmware", "Mouse"]);
  assert.match(F.summary([], true), /up to date/);
  assert.match(F.summary([{}, {}], true), /2 firmware updates/);
  assert.match(F.summary([], false), /Looking/);
  assert.equal(F.result(2, "").ok, true);
  assert.match(F.result(1, "Not authorized").message, /not allowed/);
  assert.match(F.result(1, "Cannot install update when not on AC power").message, /charger/);
  assert.match(F.result(1, "line\nDevice busy").message, /Device busy/);
});
