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
            add: "Adding the account…", remove: "Removing the account…" })[operation] || "Working…";
}
