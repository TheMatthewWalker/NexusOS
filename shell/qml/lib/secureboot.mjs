// Settings, About, Secure Boot: each step of nexus-secure-boot status, in words.
// Tested with Node (tests/shell/secureboot.test.mjs).

export const PLAN = {
  description: "Turn on Secure Boot: sign the start-up menu and kernels with this computer's own keys",
  changes: [{ option: "nexusos.secureBoot.enable", enabled: true }]
};

export function parse(text) { try { const v = JSON.parse(String(text || "")); return v && v.step ? v : null } catch (e) { return null } }

// { detail, action }: action is "propose", "firmware", "enroll" or "".
export function step(s) {
  if (!s) return { detail: "…", action: "" };
  switch (s.step) {
  case "on": return { detail: "On. The computer only starts software signed with trusted keys.", action: "" };
  case "unsupported": return { detail: "This computer started without UEFI, so Secure Boot is not available.", action: "" };
  case "prepare": return { detail: "Off. Turning it on protects the start-up from tampering; it takes a few restarts and a visit to the firmware settings.", action: "propose" };
  case "setup": return { detail: "Step 2 of 3: restart into the firmware settings, find Secure Boot, and choose to clear or reset its keys (Setup Mode). Save, and start NexusOS again.", action: "firmware" };
  case "enroll": return { detail: "Step 3 of 3: the firmware is ready for this computer's keys.", action: "enroll" };
  case "turn-on": return { detail: "Almost done: restart into the firmware settings and switch Secure Boot on.", action: "firmware" };
  default: return { detail: "", action: "" };
  }
}
