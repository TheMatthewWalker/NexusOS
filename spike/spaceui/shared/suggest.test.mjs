// Run: node --test spike/spaceui/shared/  (after scripts/spike/build_shared.py)
import test from "node:test";
import assert from "node:assert/strict";
import { APP_IDS, suggestApps, slot, emergence, cubicBezier, entryEase, launcherPlanet, envelope } from "./suggest.mjs";
import { tokens } from "./tokens.mjs";

const close = (a, b, eps = 1e-6) => assert.ok(Math.abs(a - b) < eps, `${a} != ${b}`);

test("keyword suggestions match the prototype", () => {
  assert.deepEqual(suggestApps(""), []);
  assert.deepEqual(suggestApps("   "), []);
  assert.deepEqual(suggestApps("help me research sources"), ["browser", "files"]);
  assert.deepEqual(suggestApps("debug my python script"), ["terminal", "files"]);
  assert.deepEqual(suggestApps("turn the volume down"), ["settings"]);
  assert.deepEqual(suggestApps("hello there"), []);
});

test("slots sit on the token radius at the token angles", () => {
  const o = tokens.baseline.appOrbit;
  for (const id of APP_IDS) {
    const { x, y } = slot(id);
    close(Math.hypot(x, y), o.radius);
    const diff = ((Math.atan2(y, x) * 180) / Math.PI - o.angles[id]) % 360;
    close(Math.min(Math.abs(diff), 360 - Math.abs(diff)), 0);
  }
});

test("emergence starts at the core, passes the waypoint and settles on the slot", () => {
  for (const id of APP_IDS) {
    const { x, y } = slot(id);
    const start = emergence(id, 0), mid = emergence(id, 0.45), end = emergence(id, 1);
    close(start.x, 0); close(start.y, 0); close(start.scale, 0.08); close(start.opacity, 0);
    close(mid.x, x * 0.46 - y * 0.24); close(mid.y, y * 0.46 + x * 0.24); close(mid.scale, 0.62);
    close(end.x, x); close(end.y, y); close(end.scale, 1); close(end.opacity, 1);
  }
  assert.deepEqual(emergence("files", -1), emergence("files", 0));
  assert.deepEqual(emergence("files", 2), emergence("files", 1));
});

test("cubic-bezier matches CSS reference values", () => {
  const linear = cubicBezier(0, 0, 1, 1);
  for (const x of [0, 0.25, 0.5, 0.9, 1]) close(linear(x), x, 1e-4);
  const ease = cubicBezier(0.25, 0.1, 0.25, 1); // CSS "ease"
  close(ease(0.5), 0.8024, 1e-3);
  close(entryEase(0), 0); close(entryEase(1), 1);
  let prev = 0;
  for (let i = 1; i <= 100; i++) { const v = entryEase(i / 100); assert.ok(v >= prev - 1e-9); prev = v; }
});

test("launcher planets stay above the screen edge", () => {
  for (const expanded of [false, true])
    for (let n = 1; n <= 6; n++)
      for (let i = 0; i < n; i++) assert.ok(launcherPlanet(i, n, expanded).y < 0);
  assert.ok(launcherPlanet(0, 1, true).size >= tokens.handoverRecommendations.desktopTargetMinimumApprox);
});

test("simulated envelope stays in range", () => {
  for (let t = 0; t < 20; t += 0.01) { const v = envelope(t); assert.ok(v >= 0 && v <= 1); }
});
