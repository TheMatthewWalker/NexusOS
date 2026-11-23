import { test } from "node:test";
import assert from "node:assert/strict";
import * as O from "./onscreen.mjs";

test("volume steps by 5% between 0 and 100%", () => {
  assert.equal(O.step(0.5, true), 0.55);
  assert.equal(O.step(0.98, true), 1);
  assert.equal(O.step(1.4, true), 1);           // a stream at 140% comes back to 100%
  assert.equal(O.step(0.03, false), 0);
  assert.equal(O.step(0.52, false), 0.45);      // onto the next whole step
  assert.equal(O.brightnessStep(0.05, false), 0.05);
});

test("brightness is read from brightnessctl; labels", () => {
  assert.deepEqual(O.parseBrightness("intel_backlight,backlight,4800,50%,9600\n"), { device: "intel_backlight", value: 0.5 });
  assert.equal(O.parseBrightness(""), null);
  assert.equal(O.label("volume", 0.4, false), "40%");
  assert.equal(O.label("volume", 0.4, true), "Muted");
  assert.equal(O.label("microphone", 1, true), "Microphone off");
});

test("caps lock is read from the main keyboard", () => {
  assert.equal(O.capsLock(JSON.stringify({ keyboards: [{ name: "a", capsLock: true, main: false }, { name: "b", capsLock: false, main: true }] })), false);
  assert.equal(O.capsLock(JSON.stringify({ keyboards: [{ name: "a", capsLock: true }] })), true);
  assert.equal(O.capsLock("not json"), null);
  assert.equal(O.capsLock(JSON.stringify({ keyboards: [] })), null);
  assert.equal(O.label("capslock", 1, false), "Caps Lock on");
  assert.ok(!O.hasBar("capslock") && O.hasBar("volume"));
});
