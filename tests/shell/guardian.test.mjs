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
