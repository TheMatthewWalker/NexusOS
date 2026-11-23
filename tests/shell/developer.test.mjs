import { test } from "node:test";
import assert from "node:assert/strict";
import * as D from "./developer.mjs";

test("SSH public keys are listed by file", () => {
  const k = D.keys(["== /home/ada/.ssh/id_ed25519.pub", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample ada@laptop",
                    "== /home/ada/.ssh/work.pub", "not a key", "== /home/ada/.ssh/old.pub",
                    "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQ"].join("\n"));
  assert.deepEqual(k.map(x => [x.file.split("/").pop(), x.type, x.comment]), [["id_ed25519.pub", "ed25519", "ada@laptop"], ["old.pub", "rsa", ""]]);
  assert.equal(D.keys("").length, 0);
});

test("project commands and the virtual-machine change", () => {
  assert.equal(D.initCommand("rust"), "nexus-dev init rust");
  assert.equal(D.initCommand("; rm -rf /"), "");
  assert.deepEqual(D.VM_PLAN.changes, [{ option: "nexusos.developer.virtualMachines.enable", enabled: true }]);
});
