// Copied next to the built wifi.mjs by tests/shell/test_shell_build.py.
import test from "node:test";
import assert from "node:assert/strict";
import { securityKind, signalLabel, rows, clickAction, explanation, passwordProblem, failMessage, stateLabel } from "./wifi.mjs";

const net = (name, extra = {}) => ({ name, signal: 0.5, security: "password", connected: false, known: false, state: "Disconnected", ...extra });

test("Quickshell security types map to what the panel can do", () => {
  for (const s of ["Open", "Owe"]) assert.equal(securityKind(s), "open");
  for (const s of ["WpaPsk", "Wpa2Psk", "Sae"]) assert.equal(securityKind(s), "password");
  for (const s of ["WpaEap", "Wpa2Eap", "Wpa3SuiteB192", "Leap", "DynamicWep"]) assert.equal(securityKind(s), "enterprise");
  for (const s of ["StaticWep", "Unknown", undefined]) assert.equal(securityKind(s), "unsupported");
});

test("signal strength reads as a word, and an unknown one as nothing", () => {
  assert.equal(signalLabel(0.9), "Strong");
  assert.equal(signalLabel(0.5), "Good");
  assert.equal(signalLabel(0.1), "Weak");
  assert.equal(signalLabel(NaN), "");
  assert.equal(signalLabel(undefined), "");
});

test("connected first, then saved, then strongest; hidden and duplicate names dropped", () => {
  const list = rows([
    net("Cafe", { signal: 0.9 }),
    net("", { signal: 1 }),
    net("Home", { signal: 0.3, connected: true }),
    net("Neighbour", { signal: 0.7 }),
    net("Work", { signal: 0.2, known: true }),
    net("Neighbour", { signal: 0.4 }),
  ]);
  assert.deepEqual(list.map(n => n.name), ["Home", "Work", "Cafe", "Neighbour"]);
  assert.equal(list[3].signal, 0.7);
});

test("the list is capped", () => {
  const many = Array.from({ length: 20 }, (_, i) => net("N" + i, { signal: i / 20 }));
  assert.equal(rows(many).length, 8);
  assert.equal(rows(many, 3)[0].name, "N19");
  assert.deepEqual(rows(null), []);
});

test("clicking a network connects, asks for a password, or explains", () => {
  assert.equal(clickAction(net("Home", { connected: true })), "details");
  assert.equal(clickAction(net("Cafe", { security: "open" })), "connect");
  assert.equal(clickAction(net("Home", { known: true })), "connect");
  assert.equal(clickAction(net("Home")), "password");
  assert.equal(clickAction(net("Uni", { security: "enterprise" })), "explain");
  assert.equal(clickAction(net("Old", { security: "unsupported" })), "explain");
  assert.match(explanation(net("Uni", { security: "enterprise" })), /nmtui/);
});

test("passwords are checked before they are sent", () => {
  assert.notEqual(passwordProblem("", "Wpa2Psk"), "");
  assert.notEqual(passwordProblem("short", "Wpa2Psk"), "");
  assert.equal(passwordProblem("correct horse", "Wpa2Psk"), "");
  assert.equal(passwordProblem("a".repeat(63), "WpaPsk"), "");
  assert.notEqual(passwordProblem("z".repeat(64), "WpaPsk"), "");
  assert.equal(passwordProblem("0123456789abcdef".repeat(4), "Wpa2Psk"), "");
  assert.equal(passwordProblem("short", "Sae"), "");
  assert.notEqual(passwordProblem("", "Sae"), "");
});

test("failures and states read as plain sentences", () => {
  assert.match(failMessage("NoSecrets"), /password/i);
  assert.match(failMessage("WifiAuthTimeout"), /did not answer/);
  assert.equal(failMessage("Unknown"), "Could not connect.");
  assert.equal(stateLabel(net("Home", { connected: true })), "Connected");
  assert.equal(stateLabel(net("Home", { state: "Connecting" })), "Connecting…");
  assert.equal(stateLabel(net("Home", { known: true })), "Saved");
  assert.equal(stateLabel(net("Home")), "");
});
