// Copied next to the built guardian.mjs by tests/shell/test_shell_build.py.
import test from "node:test";
import assert from "node:assert/strict";
import * as G from "./guardian.mjs";

const ID = "1a2b3c4d5e6f708192a3b4c5";
const BUILD = "ab".repeat(32);
const record = {
  candidateId: ID, buildHash: BUILD, staged: false, risk: { level: "review", notes: [] },
  settings: [{ option: "services.printing.enable", summary: "CUPS printing service", before: false, after: true }],
  packages: [{ package: "hello", before: null }],
};
const helper = "/run/current-system/sw/bin/nexus-guardian-desktop-approve";
const message = id => "Approve the Guardian system change and build named here: " + helper + " " + id + ". It is used from the next restart.";

test("the title names the candidate and how much changes", () => {
  assert.equal(G.title(record), "Approve system change 1a2b3c4d: 1 setting, 1 package");
  assert.equal(G.title({ ...record, packages: [], settings: [record.settings[0], { ...record.settings[0], option: "x" }] }),
               "Approve system change 1a2b3c4d: 2 settings");
  assert.equal(G.title(null), "");
  assert.equal(G.title({ ...record, buildHash: "short" }), "");
});

test("items come from the broker's record", () => {
  assert.deepEqual(G.items(record), [
    { kind: "setting", name: "services.printing.enable", label: "CUPS printing service", before: false, after: true },
    { kind: "package", name: "hello", label: "hello", before: null, after: true },
  ]);
});

test("Core's summary must name the same changes as the broker's", () => {
  const core = [{ kind: "package", name: "hello", after: true }, { kind: "setting", name: "services.printing.enable", after: true }];
  assert.equal(G.agrees(core, record), true);
  assert.equal(G.agrees([core[0]], record), false);                                         // one missing
  assert.equal(G.agrees([core[0], { ...core[1], after: false }], record), false);          // another value
  assert.equal(G.agrees([core[0], { ...core[1], name: "services.flatpak.enable" }], record), false);
  assert.equal(G.agrees(core.concat([{ kind: "package", name: "evil", after: true }]), record), false);
  assert.equal(G.agrees(core, null), false);
});

test("the helper is given the candidate and the build", () => {
  assert.deepEqual(G.helperArgs(record), [ID, BUILD]);
  assert.deepEqual(G.helperArgs({}), []);
});

test("only Guardian's approval of this exact candidate and build is the dialog's", () => {
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, message(ID + " " + BUILD), record), true);
  assert.equal(G.ownsFlow("org.freedesktop.udisks2.filesystem-mount", message(ID + " " + BUILD), record), false);
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, message("ffffffffffffffffffffffff " + BUILD), record), false);  // another candidate
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, message(ID + " " + "cd".repeat(32)), record), false);         // another build
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, message(ID), record), false);                                  // no build named
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, message(ID + " " + BUILD + " ffffffffffffffffffffffff"), record), false);
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, "Enter your password to approve this system change.", record), false);
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, message(ID + " " + BUILD), null), false);
  assert.equal(G.ownsFlow(G.APPROVE_ACTION, undefined, record), false);
});

const LOCK = "cd".repeat(32);
const update = {
  kind: "update", candidateId: ID, buildHash: BUILD, staged: false, risk: { level: "review", notes: [] },
  settings: [], packages: [], nixModule: "",
  update: {
    lockHash: LOCK, previousLockHash: "ef".repeat(32), otherPackages: 10, added: 1, removed: 0,
    nixpkgs: { branch: "nixos-26.05", before: { rev: "7993f34ee47e28a3d607a525aadc6c94b73be728", lastModified: 1791765022 },
               after: { rev: "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567", lastModified: 1792900000 } },
    packages: [{ name: "linux", label: "Linux", before: "6.18.56", after: "6.18.60" },
               { name: "firefox", label: "Firefox", before: "144.0", after: "144.0.2" }],
  },
};

test("a security update's record names its packages and NixOS revisions", () => {
  assert.equal(G.isUpdate(update), true);
  assert.equal(G.isUpdate(record), false);
  assert.equal(G.title(update), "Approve security updates 1a2b3c4d: 12 packages updated");
  assert.equal(G.updateLine(update.update),
               "Security updates: 12 packages updated, including Linux 6.18.56 → 6.18.60, Firefox 144.0 → 144.0.2");
  assert.equal(G.revisions(update.update), "NixOS nixos-26.05: 7993f34 (2026-10-12) → 0a1b2c3 (2026-10-25)");
  assert.deepEqual(G.items(update), [
    { kind: "update", name: "linux", label: "Linux", before: "6.18.56", after: "6.18.60" },
    { kind: "update", name: "firefox", label: "Firefox", before: "144.0", after: "144.0.2" },
  ]);
  assert.equal(G.updateLine({ ...update.update, packages: null }), "Security updates (the list of updated packages could not be read)");
  assert.equal(G.title({ ...update, update: { ...update.update, lockHash: "x" } }), "");
});

test("a NexusOS update says so, with the NexusOS commits before the NixOS ones", () => {
  const nexus = { ...update, update: { ...update.update, nexusos: { source: "github:TheMatthewWalker/NexusOS/release",
    before: { rev: "1".repeat(40), lastModified: 1791000000 }, after: { rev: "2".repeat(40), lastModified: 1792000000 } } } };
  assert.equal(G.isNexusUpdate(nexus.update), true);
  assert.equal(G.isNexusUpdate(update.update), false);
  assert.equal(G.title(nexus), "Approve the NexusOS update 1a2b3c4d: 12 packages updated");
  assert.match(G.updateLine(nexus.update), /^NexusOS 2026-10-\d\d, with security updates: 12 packages updated/);
  assert.match(G.revisions(nexus.update), /^NexusOS: 1111111 \(.+\) → 2222222 \(.+\); NixOS nixos-26.05: /);
});

test("Core's update must name the same lock and versions as the broker's", () => {
  const core = G.items(update);
  assert.equal(G.agrees(core, update, LOCK), true);
  assert.equal(G.agrees(core, update, "ab".repeat(32)), false);                  // another lock
  assert.equal(G.agrees(core, update), false);                                  // no lock named
  assert.equal(G.agrees([core[0]], update, LOCK), false);
  assert.equal(G.agrees([core[0], { ...core[1], after: "145.0" }], update, LOCK), false);
  assert.deepEqual(G.helperArgs(update), [ID, BUILD]);                          // approved the same way
});
