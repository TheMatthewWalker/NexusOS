// Settings, Developer (DeveloperPage.qml). Tested with Node (tests/shell/developer.test.mjs).

export const VM_PLAN = {
  description: "Turn on virtual machines (KVM, QEMU and virt-manager)",
  changes: [{ option: "nexusos.developer.virtualMachines.enable", enabled: true }]
};

// `for f in ~/.ssh/*.pub; do echo "== $f"; cat "$f"; done`: the public keys.
export function keys(text) {
  const out = [];
  let file = "";
  for (const line of String(text || "").split("\n")) {
    const f = /^== (.+\.pub)$/.exec(line);
    if (f) { file = f[1]; continue }
    const m = /^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com) (\S+)(?: (.*))?$/.exec(line.trim());
    if (m && file) out.push({ file, type: m[1].replace(/^ssh-|@openssh\.com$/g, ""), comment: m[3] || "", key: line.trim() });
  }
  return out;
}

export const LANGUAGES = ["python", "node", "rust", "go", "java", "dotnet", "c"];
export function initCommand(lang) { return LANGUAGES.includes(lang) ? "nexus-dev init " + lang : "" }
