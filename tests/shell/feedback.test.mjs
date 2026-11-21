import { test } from "node:test";
import assert from "node:assert/strict";
import * as F from "./feedback.mjs";

const json = JSON.stringify([
  { time: 1763650000000000, pid: 4242, uid: 1000, sig: 11, exe: "/nix/store/" + "a".repeat(32) + "-firefox-133.0/lib/firefox/.firefox-wrapped" },
  { time: 1763660000000000, pid: 77, uid: 1000, sig: 6, exe: "/home/ada/bin/secret-project/tool" },
  { pid: 1 }
]);

test("crashes are named without private paths", () => {
  const c = F.crashes(json, "/home/ada");
  assert.equal(c.length, 2);
  assert.equal(c[0].program, "tool (from your home folder)");
  assert.equal(c[0].signal, "SIGABRT");
  assert.equal(c[1].program, "firefox (firefox-133.0)");
  assert.equal(c[1].name, "firefox");
  assert.equal(c[1].when, 1763650000000);
  assert.deepEqual(F.newSince(c, 1763655000000).map(x => x.id), ["77-1763660000000000"]);
  assert.deepEqual(F.crashes("nope"), []);
});

test("a report holds only what was chosen", () => {
  const crash = F.crashes(json, "/home/ada")[1];
  const r = F.report({ text: "", crash, system: { NexusOS: "NexusOS 0.2.0", Memory: "16 GB", Graphics: "" } });
  assert.equal(r.title, "firefox stopped unexpectedly");
  assert.match(r.body, /- Program: firefox \(firefox-133.0\)\n- Stopped with: SIGSEGV/);
  assert.match(r.body, /- Memory: 16 GB/);
  assert.doesNotMatch(r.body, /Graphics/);
  const plain = F.report({ text: "The dock flickers\nwhen I plug in a screen" });
  assert.equal(plain.title, "The dock flickers");
  assert.doesNotMatch(plain.body, /This computer|Crash/);
});

test("the issue address stays short enough", () => {
  const url = F.issueUrl("Feedback", "x".repeat(20000));
  assert.ok(url.length <= 7000);
  assert.ok(url.startsWith(F.ISSUES + "?title=Feedback&body="));
  assert.equal(new URL(F.issueUrl("a&b", "c d")).searchParams.get("title"), "a&b");
});
