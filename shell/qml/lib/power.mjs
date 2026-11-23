// Settings, Power and Idle.qml: the choices and logind's sleep signal.
// Tested with Node (tests/shell/power.test.mjs).

export const SCREEN_OFF = [0, 2, 5, 10, 15, 30];
export const SLEEP = [0, 5, 10, 15, 20, 30, 60, 120];
export const LID = ["sleep", "lock", "nothing"];

export function minutesLabel(m) {
  if (!m) return "Never";
  if (m < 60) return m + " min";
  return (m / 60) + (m === 60 ? " hour" : " hours");
}

export function options(values) { return values.map(v => ({ value: v, label: minutesLabel(v) })) }

export function lidLabel(action) {
  return ({ sleep: "Sleep", lock: "Lock the screen", nothing: "Do nothing" })[action] || "Sleep";
}

// What the Power page says about sleep now, with the numbers in words.
export function sleepDetail(battery, plugged, onBattery, hasBattery) {
  const now = hasBattery && onBattery ? battery : plugged;
  const part = m => (m ? "after " + minutesLabel(m).replace(" min", " minutes") : "never");
  if (!hasBattery) return "The computer sleeps " + part(plugged) + " with nothing touched. The screen locks first.";
  return "On battery it sleeps " + part(battery) + "; plugged in, " + part(plugged) + ". The screen locks first." +
    (now ? "" : " Right now it does not sleep by itself.");
}

// gdbus monitor prints one line per signal, for example
//   /org/freedesktop/login1: org.freedesktop.login1.Manager.PrepareForSleep (true,)
// PrepareForSleep is true before sleep and false after waking; any other line is null.
export function sleepSignal(line) {
  const m = /org\.freedesktop\.login1\.Manager\.PrepareForSleep \((true|false),?\)/.exec(String(line || ""));
  return m ? m[1] === "true" : null;
}

// Battery care: a system change through Guardian (nix/modules/battery.nix).
export function chargeLimitPlan(on) {
  return { description: on ? "Stop charging the battery at 80% (battery care)" : "Charge the battery to 100% again",
           changes: [{ option: "nexusos.power.chargeLimit.enable", enabled: !!on }] };
}
// `cat /sys/class/power_supply/BAT*/charge_control_end_threshold`: the limit now, or null where the firmware has none.
export function chargeLimit(text) {
  const n = parseInt(String(text || "").trim().split("\n")[0]);
  return isNaN(n) || n < 1 || n > 100 ? null : n;
}

// `systemctl reboot --boot-loader-entry=help`: the boot menu's entries. systemd-boot
// finds Windows on the same EFI partition by itself ("auto-windows").
export function windowsEntry(text) {
  const entries = String(text || "").split("\n").map(l => l.trim()).filter(l => /^[A-Za-z0-9._@+-]+$/.test(l));
  return entries.includes("auto-windows") ? "auto-windows" : (entries.find(e => /^windows/i.test(e)) || "");
}
