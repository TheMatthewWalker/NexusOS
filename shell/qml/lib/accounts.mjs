// Settings, Accounts (AccountsPage.qml): the rules the page checks before it
// asks nexus-account, which checks them all again. Tested with Node
// (tests/shell/accounts.test.mjs).

export const MIN_PASSWORD = 10;
const RESERVED = ["root", "nixos", "nobody", "nexus-guardian", "greeter"];

export function passwordProblem(password, again) {
  if (!password) return "Choose a password.";
  if (password.length < MIN_PASSWORD) return "Use at least " + MIN_PASSWORD + " characters: it also approves system changes.";
  if (/[\n\0]/.test(password)) return "A password cannot contain a line break.";
  if (password !== again) return "The two passwords are not the same.";
  return "";
}

export function loginProblem(login) {
  if (!login) return "Choose a login name.";
  if (!/^[a-z_][a-z0-9_-]{0,31}$/.test(login)) return "Login names use lower case letters, digits, - and _, and start with a letter.";
  if (RESERVED.includes(login)) return login + " is reserved; pick another name.";
  return "";
}

// "Ada Lovelace" -> "ada"
export function suggestLogin(fullName) {
  const first = String(fullName || "").trim().split(/\s+/)[0] || "";
  const ascii = first.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9_-]/g, "");
  return /^[a-z_]/.test(ascii) ? ascii.slice(0, 32) : "";
}

export function title(account, me) {
  const name = account.fullName || account.login;
  return account.login === me ? name + " (you)" : name;
}

// The page offers Remove for others' accounts only; the helper also keeps the first account.
export function removable(account, me) { return account.login !== me }

export function parse(text) {
  try { return JSON.parse(String(text || "").trim().split("\n").pop() || "{}") } catch (e) { return {} }
}

// pkexec's own exit codes: 126 the password was refused or the prompt closed, 127 not allowed.
export function refusal(code) {
  if (code === 126) return "Not changed: the password prompt was closed or the password was wrong.";
  if (code === 127) return "Not allowed: this account cannot do that.";
  return "Something went wrong (exit " + code + ").";
}

export function working(operation) {
  return ({ password: "Changing your password…", disk: "Changing the disk's password…",
            add: "Adding the account…", remove: "Removing the account…",
            tpm: "Setting up the TPM…" })[operation] || "Working…";
}

// Cloud drives (nexus-cloud): the folder's name, as the script checks it.
export function driveNameOk(name) { return /^[A-Za-z][A-Za-z0-9_-]{0,29}$/.test(String(name || "")) }
// "OneDrive", or "OneDrive2" when there is one already.
export function freeDriveName(base, drives) {
  const taken = new Set((drives || []).map(d => d.name));
  if (!taken.has(base)) return base;
  for (let n = 2; ; n++) if (!taken.has(base + n)) return base + n;
}

// Fingerprint sign-in (nix/modules/fingerprint.nix): a Guardian change to turn on.
export const FINGERPRINT_PLAN = {
  description: "Use a fingerprint for the lock screen and approvals",
  changes: [{ option: "nexusos.fingerprint.enable", enabled: true }]
};
// One line of fprintd-enroll's output -> what to tell the person, or "" to show nothing.
export function enrollLine(line) {
  const l = String(line || "");
  if (/enroll-completed/.test(l)) return "Done: your fingerprint is saved.";
  if (/enroll-stage-passed/.test(l)) return "Good. Lift your finger and touch the reader again.";
  if (/enroll-retry-scan|enroll-swipe-too-short|enroll-finger-not-centered|enroll-remove-and-retry/.test(l)) return "Not quite: touch the reader again, a little differently.";
  if (/enroll-duplicate/.test(l)) return "That finger is already saved.";
  if (/No devices available/i.test(l)) return "No fingerprint reader was found on this computer.";
  if (/enroll-failed|enroll-data-full|enroll-disconnected|enroll-unknown-error/.test(l)) return "That did not work. Try again.";
  if (/Enrolling/.test(l)) return "Touch the fingerprint reader with your finger, again each time it asks.";
  return "";
}
