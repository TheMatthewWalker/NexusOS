import { test } from "node:test";
import assert from "node:assert/strict";
import * as B from "./backups.mjs";

const now = new Date(2026, 10, 20, 15, 0);

test("when the last backup was, in words", () => {
  assert.equal(B.whenLabel(new Date(2026, 10, 20, 9, 5), now), "today at 09:05");
  assert.equal(B.whenLabel(new Date(2026, 10, 19, 23, 0), now), "yesterday at 23:00");
  assert.equal(B.whenLabel(new Date(2026, 10, 15, 12, 0), now), "5 days ago");
  assert.equal(B.whenLabel(new Date(2026, 2, 12, 12, 0), now), "12 March 2026");
  assert.equal(B.whenLabel("not a date", now), "");
});

test("the status line says what to do", () => {
  assert.match(B.statusLine({ setUp: false }, now), /Plug in a drive/);
  const last = Math.floor(new Date(2026, 10, 20, 9, 5) / 1000);
  assert.equal(B.statusLine({ setUp: true, drivePresent: true, lastBackup: last }, now), "Last backup today at 09:05.");
  assert.match(B.statusLine({ setUp: true, drivePresent: false, driveName: "USB", lastBackup: last }, now), /Plug in USB/);
  assert.match(B.statusLine({ setUp: true, state: "failed", message: "drive full" }, now), /did not finish: drive full/);
  assert.equal(B.overdue({ setUp: true, lastBackup: last }, now), false);
  assert.equal(B.overdue({ setUp: true }, now), true);
  assert.equal(B.overdue({ setUp: false }, now), false);
});

test("passwords, sizes, folders and output", () => {
  assert.match(B.passwordProblem("short", "short"), /at least 10/);
  assert.match(B.passwordProblem("long enough one", "long enough two"), /not the same/);
  assert.equal(B.passwordProblem("long enough one", "long enough one"), "");
  assert.equal(B.sizeLabel(64e9), "64 GB");
  assert.equal(B.sizeLabel(1.5e12), "1.5 TB");
  assert.equal(B.restorePath("/home/ada", "Documents"), "/home/ada/Documents");
  assert.equal(B.restorePath("/home/ada", ""), "");
  assert.equal(B.restorePath("/home/ada", "../etc"), "");
  assert.equal(B.folderLabel(""), "Everything");
  assert.deepEqual(B.parse('progress\n{"error": "x"}'), { error: "x" });
  assert.deepEqual(B.parse("nonsense"), {});
  assert.match(B.driveLabel({ name: "USB", freeBytes: 2e9, totalBytes: 64e9, hasBackup: true }), /USB · 2 GB free of 64 GB · has a NexusOS backup/);
});
