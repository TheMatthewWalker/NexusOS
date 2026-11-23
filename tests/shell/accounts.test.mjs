import { test } from "node:test";
import assert from "node:assert/strict";
import * as A from "./accounts.mjs";

test("passwords follow the installer's rule and must match", () => {
  assert.match(A.passwordProblem("", ""), /Choose/);
  assert.match(A.passwordProblem("short", "short"), /10 characters/);
  assert.match(A.passwordProblem("long-enough-1", "long-enough-2"), /not the same/);
  assert.equal(A.passwordProblem("long-enough-1", "long-enough-1"), "");
});

test("login names are checked and suggested from the name", () => {
  assert.equal(A.loginProblem("ada"), "");
  assert.match(A.loginProblem("Ada"), /lower case/);
  assert.match(A.loginProblem("root"), /reserved/);
  assert.equal(A.suggestLogin("Zoë Smith"), "zoe");
  assert.equal(A.suggestLogin("  "), "");
  assert.equal(A.suggestLogin("1st"), "");
});

test("you are named, never removable, and pkexec refusals are said plainly", () => {
  assert.equal(A.title({ login: "ada", fullName: "Ada" }, "ada"), "Ada (you)");
  assert.equal(A.removable({ login: "ada" }, "ada"), false);
  assert.equal(A.removable({ login: "bob" }, "ada"), true);
  assert.match(A.refusal(126), /password was wrong/);
  assert.deepEqual(A.parse('noise\n{"message":"ok"}'), { message: "ok" });
  assert.deepEqual(A.parse("not json"), {});
});

test("cloud drive folder names", () => {
  assert.ok(A.driveNameOk("OneDrive"));
  for (const bad of ["", "1x", "a b", "../x", "x".repeat(31)]) assert.ok(!A.driveNameOk(bad), bad);
  assert.equal(A.freeDriveName("OneDrive", []), "OneDrive");
  assert.equal(A.freeDriveName("OneDrive", [{ name: "OneDrive" }, { name: "OneDrive2" }]), "OneDrive3");
});
