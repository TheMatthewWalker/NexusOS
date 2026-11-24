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

test("battery care is a Guardian change, shown only where the firmware has a limit", () => {
  assert.deepEqual(P.chargeLimitPlan(true).changes, [{ option: "nexusos.power.chargeLimit.enable", enabled: true }]);
  assert.equal(P.chargeLimitPlan(false).changes[0].enabled, false);
  assert.equal(P.chargeLimit("80\n"), 80);
  assert.equal(P.chargeLimit(""), null);
  assert.equal(P.chargeLimit("cat: No such file"), null);
});

test("Windows beside NexusOS is found in the boot menu's entries", () => {
  assert.equal(P.windowsEntry("nixos-generation-12.conf\nauto-windows\nauto-reboot-to-firmware-setup\n"), "auto-windows");
  assert.equal(P.windowsEntry("nixos-generation-12.conf\n"), "");
  assert.equal(P.windowsEntry("windows-11.conf\n"), "windows-11.conf");
  assert.equal(P.windowsEntry("auto-windows; rm -rf ~\n"), "");
});

test("hibernate is a Guardian change, and logind's answer reads plainly", () => {
  assert.deepEqual(P.HIBERNATE_PLAN.changes, [{ option: "nexusos.hibernate.enable", enabled: true }]);
  assert.equal(P.canHibernate('s "yes"\n'), "yes");
  assert.equal(P.canHibernate('s "na"'), "na");
  assert.equal(P.canHibernate(""), "");
});
