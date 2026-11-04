// Copied next to shell/qml/lib/installer.mjs by tests/installer/test_graphical_installer.py.
import test from "node:test";
import assert from "node:assert/strict";
import * as I from "./installer.mjs";

const GiB = 1073741824;
const disk = (extra = {}) => ({ path: "/dev/nvme0n1", size: 512 * GiB, model: "Samsung SSD 980 PRO 512GB", transport: "nvme",
                                removable: false, inUse: false, alongside: null, ...extra });

test("sizes read as Windows shows them", () => {
  assert.equal(I.sizeLabel(512 * GiB), "512 GB");
  assert.equal(I.sizeLabel(1.5 * GiB), "1.5 GB");
  assert.equal(I.sizeLabel(2000398934016), "1.8 TB");
  assert.equal(I.sizeLabel(300 * 1048576), "300 MB");
  assert.equal(I.sizeLabel(undefined), "");
});

test("disks are named by model and size, and the installer's own stick says so", () => {
  assert.equal(I.diskTitle(disk()), "Samsung SSD 980 PRO 512GB (512 GB)");
  assert.equal(I.diskTitle(disk({ model: "" })), "/dev/nvme0n1 (512 GB)");
  assert.equal(I.diskDetail(disk()), "/dev/nvme0n1 · NVMe");
  assert.match(I.diskDetail(disk({ path: "/dev/sda", transport: "usb", removable: true, inUse: true })), /USB, removable · the installer is running from it/);
});

test("what can be done with a disk follows nexus-install's plan", () => {
  const min = 32 * GiB;
  const plan = { possible: true, reason: "NexusOS goes in the 120 GB of free space…", systems: "Windows", warnings: ["BitLocker…"], esp: "/dev/nvme0n1p1", freeBytes: 120 * GiB };
  let c = I.diskChoices(disk({ alongside: plan }), min);
  assert.equal(c.erase.possible, true);
  assert.equal(c.alongside.possible, true);
  assert.deepEqual(c.alongside.warnings, ["BitLocker…"]);
  c = I.diskChoices(disk({ alongside: { possible: false, reason: "Make room in Windows first (Disk Management, Shrink volume)" } }), min);
  assert.equal(c.alongside.possible, false);
  assert.match(c.alongside.reason, /Shrink volume/);
  c = I.diskChoices(disk({ inUse: true, alongside: plan }), min);
  assert.equal(c.erase.possible, false);
  assert.equal(c.alongside.possible, false);
  c = I.diskChoices(disk({ size: 16 * GiB }), min);
  assert.match(c.erase.reason, /too small/);
  assert.equal(I.diskChoices(null, min).erase.possible, false);
});

test("the You step uses nexus-install's rules", () => {
  assert.equal(I.loginProblem("ada"), "");
  assert.notEqual(I.loginProblem("Ada"), "");
  assert.notEqual(I.loginProblem("1ada"), "");
  assert.match(I.loginProblem("root"), /reserved/);
  assert.match(I.loginProblem("nixos"), /reserved/);
  assert.notEqual(I.loginProblem(""), "");
  assert.equal(I.suggestLogin("Ada Lovelace"), "ada");
  assert.equal(I.suggestLogin("  Zoë  Smith"), "zoe");
  assert.equal(I.suggestLogin("123"), "");
  assert.equal(I.suggestLogin("Root"), "");
  assert.match(I.passwordProblem("short", "short"), /at least 10 characters/);
  assert.match(I.passwordProblem("0123456789", "0123456788"), /do not match/);
  assert.equal(I.passwordProblem("0123456789", "0123456789"), "");
  assert.equal(I.hostnameProblem("nexus-laptop"), "");
  assert.notEqual(I.hostnameProblem("-nexus"), "");
  assert.notEqual(I.hostnameProblem("my laptop"), "");
  assert.notEqual(I.fullNameProblem("Ada\nLovelace"), "");
  const ok = { fullName: "Ada", login: "ada", password: "0123456789", again: "0123456789", host: "nexus" };
  assert.equal(I.youReady(ok), true);
  assert.equal(I.youReady({ ...ok, again: "x" }), false);
});

test("time zones are found by city, with spaces or underscores", () => {
  const zones = ["America/New_York", "Europe/London", "Asia/Tokyo"];
  assert.deepEqual(I.filterZones(zones, "new york"), ["America/New_York"]);
  assert.deepEqual(I.filterZones(zones, "LONDON"), ["Europe/London"]);
  assert.deepEqual(I.filterZones(zones, ""), zones);
  assert.equal(I.zoneLabel("America/New_York"), "America / New York");
});

test("the flags drive nexus-install, with the password on standard input", () => {
  const s = { disk: "/dev/nvme0n1", login: "ada", fullName: "Ada Lovelace", host: "nexus", timezone: "Europe/London", alongside: true, encrypt: true };
  const args = I.installArgs(s);
  assert.deepEqual(args, ["--progress-json", "--yes", "--disk", "/dev/nvme0n1", "--user", "ada", "--password-file", "-",
                          "--hostname", "nexus", "--timezone", "Europe/London", "--full-name", "Ada Lovelace", "--alongside"]);
  const erase = I.installArgs({ ...s, alongside: false, encrypt: false, fullName: "" });
  assert.ok(!erase.includes("--alongside"));
  assert.ok(erase.includes("--no-encrypt"));
  assert.ok(!erase.includes("--full-name"));
  for (const a of [args, erase]) assert.ok(!a.some(x => /0123456789/.test(x)));
});

test("progress lines move the bar and end in done or the real error", () => {
  assert.equal(I.parseLine("No internet connection"), null);
  assert.equal(I.parseLine("{broken"), null);
  assert.equal(I.parseLine('{"no":"event"}'), null);
  let p = I.applyEvent(I.initialProgress(), I.parseLine('{"event":"start","disk":"/dev/vda","alongside":false,"encrypted":true,"log":"/tmp/nexus-install.log"}'));
  assert.equal(p.phase, "running");
  p = I.applyEvent(p, { event: "step", id: "prepare", n: 1, of: 6, title: "Preparing /dev/vda" });
  assert.equal(p.fraction, 0);
  p = I.applyEvent(p, { event: "step", id: "install", n: 4, of: 6, title: "Installing NexusOS" });
  assert.equal(p.fraction, 0.5);
  let before = p.fraction;
  for (let i = 0; i < 5000; i++) p = I.applyEvent(p, { event: "detail", text: "copying path" });
  assert.ok(p.fraction > before && p.fraction < 4 / 6, "the long step creeps forward but never past its end");
  assert.equal(p.detail, "copying path");
  const done = I.applyEvent(p, { event: "done" });
  assert.equal(done.phase, "done");
  assert.equal(done.fraction, 1);
  const failed = I.applyEvent(p, { event: "error", message: "Installing stopped: `nixos-install` failed (exit 1).", log: "/tmp/nexus-install.log", tail: ["error: x"] });
  assert.equal(failed.phase, "failed");
  assert.match(failed.error, /nixos-install/);
  assert.equal(failed.log, "/tmp/nexus-install.log");
  assert.deepEqual(failed.tail, ["error: x"]);
  assert.match(I.exitProblem(127, "Not authorized"), /administrator/);
  assert.match(I.exitProblem(1, ""), /exit 1/);
});

test("the summary shows the disk, encryption and the hardware scan's lines", () => {
  const probe = { hardware: { processor: "Intel(R) Core(TM) i5-1235U, 16 GB memory, laptop", graphics: "Intel Iris Xe (built-in drivers)", localAI: "qwen3:4b, on the processor, from the USB stick" } };
  const s = { disk: "/dev/nvme0n1", diskInfo: disk(), alongside: true, systems: "Windows", freeBytes: 120 * GiB, encrypt: true,
              fullName: "Ada", login: "ada", host: "nexus", timezone: "Europe/London" };
  const rows = Object.fromEntries(I.summaryRows(s, probe).map(r => [r.label, r]));
  assert.deepEqual(Object.keys(rows), ["Disk", "Encryption", "You", "Computer", "Time zone", "Processor", "Graphics", "Local AI"]);
  assert.match(rows.Disk.value, /Alongside Windows .* 120 GB of free space/);
  assert.ok(!rows.Disk.danger);
  assert.match(rows.Encryption.value, /^On/);
  assert.equal(rows["Local AI"].value, probe.hardware.localAI);
  const erase = Object.fromEntries(I.summaryRows({ ...s, alongside: false, encrypt: false }, probe).map(r => [r.label, r]));
  assert.match(erase.Disk.value, /^Erase Samsung/);
  assert.ok(erase.Disk.danger);
  assert.match(erase.Encryption.value, /^Off/);
});
