import { test } from "node:test";
import assert from "node:assert/strict";
import * as P from "./power.mjs";

test("choices read as people say them", () => {
  assert.equal(P.minutesLabel(0), "Never");
  assert.equal(P.minutesLabel(15), "15 min");
  assert.equal(P.minutesLabel(60), "1 hour");
  assert.equal(P.minutesLabel(120), "2 hours");
  assert.equal(P.lidLabel("lock"), "Lock the screen");
  assert.deepEqual(P.options([0, 5]).map(o => o.label), ["Never", "5 min"]);
});

test("the sleep sentence follows battery and plugged-in times", () => {
  assert.match(P.sleepDetail(15, 30, true, true), /On battery it sleeps after 15 minutes; plugged in, after 30 minutes/);
  assert.match(P.sleepDetail(15, 0, false, true), /plugged in, never.*does not sleep by itself/);
  assert.match(P.sleepDetail(15, 30, false, false), /^The computer sleeps after 30 minutes/);
});

test("logind's PrepareForSleep is read from gdbus monitor; anything else is ignored", () => {
  const signal = v => "/org/freedesktop/login1: org.freedesktop.login1.Manager.PrepareForSleep (" + v + ",)";
  assert.equal(P.sleepSignal(signal(true)), true);
  assert.equal(P.sleepSignal(signal(false)), false);
  assert.equal(P.sleepSignal("/org/freedesktop/login1: org.freedesktop.login1.Manager.SessionNew ('3', objectpath '/x')"), null);
  assert.equal(P.sleepSignal("Monitoring signals on object /org/freedesktop/login1 owned by org.freedesktop.login1"), null);
  assert.equal(P.sleepSignal(undefined), null);
});
