// Copied next to the built describe.mjs by tests/shell/test_shell_build.py.
import test from "node:test";
import assert from "node:assert/strict";
import * as D from "./describe.mjs";

test("durations round to minutes and drop unknown estimates", () => {
  assert.equal(D.duration(11400), "3 h 10 min");
  assert.equal(D.duration(2400), "40 min");
  assert.equal(D.duration(7200), "2 h");
  assert.equal(D.duration(20), "less than a minute");
  for (const v of [0, -5, NaN, null, undefined, Infinity]) assert.equal(D.duration(v), "");
});

test("bytes use decimal units", () => {
  assert.equal(D.bytes(512), "512 bytes");
  assert.equal(D.bytes(1.5e9), "1.5 GB");
  assert.equal(D.bytes(4.2e10), "42 GB");
  assert.equal(D.bytes(2e12), "2 TB");
  assert.equal(D.bytes(null), "");
  assert.equal(D.bytes(-1), "");
});

test("power sentences use UPower's own estimates only", () => {
  const bat = { available: true, present: true };
  assert.equal(D.power({}), "Power information unavailable");
  assert.equal(D.power({ available: true, present: false }), "Plugged in, no battery");
  assert.equal(D.power({ ...bat, state: "full", percentage: 1 }), "Fully charged");
  assert.equal(D.power({ ...bat, state: "discharging", percentage: 0.45, timeToEmpty: 11400 }), "About 3 h 10 min left (45%)");
  assert.equal(D.power({ ...bat, state: "discharging", timeToEmpty: 11400 }), "About 3 h 10 min left");
  assert.equal(D.power({ ...bat, state: "discharging", percentage: 0.45, timeToEmpty: 0 }), "On battery (45%)");
  assert.equal(D.power({ ...bat, state: "discharging" }), "On battery");
  assert.equal(D.power({ ...bat, state: "charging", percentage: 0.62, timeToFull: 2400 }), "Charging, full in about 40 min (62%)");
  assert.equal(D.power({ ...bat, state: "charging", timeToFull: 2400 }), "Charging, full in about 40 min");
  assert.equal(D.power({ ...bat, state: "charging", timeToFull: 0 }), "Charging");
  assert.equal(D.power({ ...bat, state: "empty" }), "Battery empty, plug in now");
  assert.equal(D.power({ ...bat, state: "pending-charge", percentage: 0.8 }), "Plugged in, not charging (80%)");
  assert.equal(D.power({ ...bat, state: "pending-discharge", percentage: 0.8 }), "On battery (80%)");
  assert.equal(D.power({ ...bat, state: "unknown", percentage: 0.3 }), "Battery at 30%");
  assert.equal(D.power({ ...bat, state: "unknown", percentage: NaN }), "Battery state unknown");
});

test("sound sentences", () => {
  assert.equal(D.sound({}), "No sound output found");
  assert.equal(D.sound({ available: true, muted: true, volume: 0.6, device: "USB headset" }), "Muted");
  assert.equal(D.sound({ available: true, volume: 0.6, device: "USB headset" }), "Playing through USB headset at 60%");
  assert.equal(D.sound({ available: true, volume: 0.6 }), "Playing at 60%");
  assert.equal(D.sound({ available: true, device: "Speakers" }), "Playing through Speakers");
  assert.equal(D.sound({ available: true, volume: NaN, device: "" }), "Playing");
});

test("network sentences", () => {
  const nm = { available: true, wifiHardware: true, wifiEnabled: true };
  assert.equal(D.network({}), "Network status unavailable");
  assert.equal(D.network({ ...nm, ssid: "Home" }), "Connected to Home");
  assert.equal(D.network({ ...nm, ssid: "Home", connectivity: "full" }), "Connected to Home");
  assert.equal(D.network({ ...nm, ssid: "Home", connectivity: "unknown" }), "Connected to Home");
  assert.equal(D.network({ ...nm, ssid: "Cafe", connectivity: "portal" }), "Connected to Cafe; this network wants you to sign in");
  assert.equal(D.network({ ...nm, ssid: "Home", connectivity: "limited" }), "Connected to Home, but the internet is not reachable");
  assert.equal(D.network({ ...nm, wired: true, connectivity: "none" }), "Connected by cable, but the internet is not reachable");
  assert.equal(D.network({ ...nm, wired: true }), "Connected by cable");
  assert.equal(D.network({ ...nm, wifiEnabled: false }), "Wi-Fi is off");
  assert.equal(D.network({ ...nm, wifiEnabled: false, wired: true }), "Connected by cable");
  assert.equal(D.network(nm), "No network");
  assert.equal(D.network({ available: true, wifiHardware: false }), "No network");
});

test("bluetooth sentences", () => {
  assert.equal(D.bluetooth({}), "No Bluetooth adapter");
  assert.equal(D.bluetooth({ available: true, enabled: false, connected: 2 }), "Off");
  assert.equal(D.bluetooth({ available: true, enabled: true, connected: 2 }), "On, 2 devices connected");
  assert.equal(D.bluetooth({ available: true, enabled: true, connected: 1 }), "On, 1 device connected");
  assert.equal(D.bluetooth({ available: true, enabled: true, connected: 0 }), "On, nothing connected");
  assert.equal(D.bluetooth({ available: true, enabled: true, connected: null }), "On");
});

test("time carries a zone hint", () => {
  assert.equal(D.utcOffset(0), "UTC");
  assert.equal(D.utcOffset(60), "UTC+1");
  assert.equal(D.utcOffset(-210), "UTC-3:30");
  assert.equal(D.utcOffset(345), "UTC+5:45");
  assert.equal(D.utcOffset(NaN), "");
  const d = new Date(2026, 9, 22, 9, 5);
  const offset = D.utcOffset(-d.getTimezoneOffset());
  assert.equal(D.time(d), `Thursday 22 October, 09:05 (${offset})`);
  assert.equal(D.time(d, "Europe/London"), `Thursday 22 October, 09:05 (Europe/London, ${offset})`);
  assert.equal(D.time(d, offset), `Thursday 22 October, 09:05 (${offset})`);
  assert.equal(D.time(new Date(NaN)), "Time unavailable");
  assert.equal(D.time(null), "Time unavailable");
});

const DF = `Filesystem         1-blocks        Used   Available Capacity Mounted on
/dev/nvme0n1p2 500000000000 300000000000 200000000000      60% /
/dev/nvme0n1p2 500000000000 300000000000 200000000000      60% /
`;
const DF_SPLIT = `Filesystem 1-blocks Used Available Capacity Mounted on
/dev/sda1 100000000000 95000000000 5000000000 95% /
/dev/sda2 1000000000000 400000000000 600000000000 40% /home
`;

test("df output parses into distinct volumes", () => {
  assert.deepEqual(D.parseDf(DF), [{ source: "/dev/nvme0n1p2", mount: "/", size: 5e11, used: 3e11, free: 2e11 }]);
  const split = D.parseDf(DF_SPLIT);
  assert.equal(split.length, 2);
  assert.equal(split[1].mount, "/home");
  assert.deepEqual(D.parseDf("Filesystem 1-blocks Used Available Capacity Mounted on\n/dev/x 1 2 3 4% /mnt/My Disk\n")[0].mount, "/mnt/My Disk");
  assert.deepEqual(D.parseDf(""), []);
  assert.deepEqual(D.parseDf(null), []);
  assert.deepEqual(D.parseDf("Filesystem 1-blocks Used Available Capacity Mounted on\ngarbage\n"), []);
});

test("disk sentences suggest only below 10% free", () => {
  assert.equal(D.disk([]), "Storage information unavailable");
  assert.equal(D.disk(null), "Storage information unavailable");
  const one = D.parseDf(DF);
  assert.equal(D.disk(one), "200 GB free of 500 GB");
  assert.equal(D.diskLow(one), false);
  const split = D.parseDf(DF_SPLIT);
  assert.equal(D.disk(split), "System: 5 GB free of 100 GB; Home: 600 GB free of 1 TB. System is running low on space");
  assert.equal(D.diskLow(split), true);
  const low = [{ source: "a", mount: "/", size: 100, used: 95, free: 5 }];
  assert.equal(D.disk(low), "5 bytes free of 100 bytes. Space is running low");
  assert.equal(D.diskLow(low), true);
  // Exactly 10% free is not low; reserved root blocks do not count against free space.
  assert.equal(D.diskLow([{ source: "a", mount: "/", size: 120, used: 90, free: 10 }]), false);
  assert.equal(D.diskLow([{ source: "a", mount: "/", size: 0, used: 0, free: 0 }]), false);
  assert.equal(D.diskLow(null), false);
});

test("display sentences", () => {
  assert.equal(D.display(1), "1 screen connected");
  assert.equal(D.display(3), "3 screens connected");
  assert.equal(D.display(0), "No screen information");
  assert.equal(D.display(undefined), "No screen information");
});
