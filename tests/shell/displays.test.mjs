import { test } from "node:test";
import assert from "node:assert/strict";
import * as D from "./displays.mjs";

const json = JSON.stringify([
  { name: "eDP-1", description: "BOE 0x0BCA (eDP-1)", make: "BOE", model: "0x0BCA", width: 2880, height: 1800, refreshRate: 60, scale: 2,
    availableModes: ["2880x1800@60.00Hz", "2880x1800@120.00Hz", "1920x1200@60.00Hz"] },
  { name: "DP-1", description: "Dell Inc. DELL U2720Q 8LXMZ13", make: "Dell Inc.", model: "DELL U2720Q", width: 3840, height: 2160, refreshRate: 60, scale: 1.5,
    availableModes: ["3840x2160@60.00Hz", "2560x1440@59.95Hz", "1920x1080@60.00Hz", "1920x1080@50.00Hz"] },
  { name: "HDMI-A-1; evil", description: "x" }
]);
const monitors = D.parse(json);

test("screens are read from hyprctl, with stable keys and their modes", () => {
  assert.equal(monitors.length, 2);
  assert.equal(monitors[0].key, "BOE 0x0BCA");
  assert.equal(monitors[0].builtIn, true);
  assert.equal(monitors[1].selector, "desc:Dell Inc. DELL U2720Q 8LXMZ13");
  assert.deepEqual(monitors[0].modes.map(m => m.value), ["2880x1800@120", "1920x1200@60"]);
  assert.equal(monitors[0].modes[0].label, "2880 × 1800 · 120 Hz");
  assert.deepEqual(D.parse("nonsense"), []);
});

test("rules place screens around the main one, from 0", () => {
  assert.deepEqual(D.rules(monitors, {}, 1), ["monitor desc:BOE 0x0BCA,preferred,0x0,1", "monitor desc:Dell Inc. DELL U2720Q 8LXMZ13,preferred,2880x0,1"]);
  const layout = { "BOE 0x0BCA": { scale: 2 }, "Dell Inc. DELL U2720Q 8LXMZ13": { place: "left", scale: 1.5, mode: "3840x2160@60" } };
  assert.deepEqual(D.rules(monitors, layout, 1), ["monitor desc:BOE 0x0BCA,preferred,2560x0,2", "monitor desc:Dell Inc. DELL U2720Q 8LXMZ13,3840x2160@60,0x0,1.5"]);
  const above = { "Dell Inc. DELL U2720Q 8LXMZ13": { place: "above", scale: 2 } };
  assert.deepEqual(D.rules(monitors, above, 1), ["monitor desc:BOE 0x0BCA,preferred,0x1080,1", "monitor desc:Dell Inc. DELL U2720Q 8LXMZ13,preferred,0x0,2"]);
  assert.deepEqual(D.rules(monitors, { "Dell Inc. DELL U2720Q 8LXMZ13": { place: "mirror" } }, 1)[1],
    "monitor desc:Dell Inc. DELL U2720Q 8LXMZ13,preferred,auto,1,mirror,eDP-1");
  assert.deepEqual(D.rules(monitors, { "BOE 0x0BCA": { place: "off" }, "Dell Inc. DELL U2720Q 8LXMZ13": { place: "main" } }, 1),
    ["monitor desc:Dell Inc. DELL U2720Q 8LXMZ13,preferred,0x0,1", "monitor desc:BOE 0x0BCA,disable"]);
  assert.deepEqual(D.rules(monitors, { "BOE 0x0BCA": { mode: "evil,1x1" } }, 1)[0], "monitor desc:BOE 0x0BCA,preferred,0x0,1");
});

test("changes keep one screen on and one main screen", () => {
  const [laptop, dell] = monitors.map(m => ({ ...m }));   // copies, as QML passes them
  let l = D.change({}, monitors, dell, "place", "main");
  assert.equal(D.main(monitors, l).key, dell.key);
  assert.equal(D.current(l, laptop, "place"), "right");
  l = D.change(l, monitors, laptop, "place", "off");
  assert.equal(D.current(l, laptop, "place"), "off");
  assert.deepEqual(D.change(l, monitors, dell, "place", "off"), l);          // the last screen on stays on
  l = D.change({}, monitors, laptop, "place", "off");                         // turning off the main screen
  assert.equal(D.main(monitors, l).key, dell.key);
  assert.deepEqual(D.change({}, monitors, laptop, "scale", 7), {});
  assert.equal(D.placeLabel("mirror"), "Same picture");
});
