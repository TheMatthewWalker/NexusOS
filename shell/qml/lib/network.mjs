// Settings, Network: VPNs and shared folders (NetworkPage.qml).
// Tested with Node (tests/shell/network.test.mjs).

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
export function validUuid(u) { return UUID.test(String(u || "")) }

// nmcli -t -f NAME,UUID,TYPE,ACTIVE connection show (":" separated; "\:" inside names).
export function vpns(text) {
  return String(text || "").split("\n").map(line => {
    const f = line.split(/(?<!\\):/).map(x => x.replace(/\\:/g, ":"));
    if (f.length < 4 || !validUuid(f[1])) return null;
    if (f[2] !== "vpn" && f[2] !== "wireguard") return null;
    return { name: f[0], uuid: f[1], kind: f[2] === "wireguard" ? "WireGuard" : "VPN", active: f[3] === "yes" };
  }).filter(Boolean);
}

// A VPN file to import: its nmcli import type by extension, or "" if not one we know.
export function importType(path) {
  const p = String(path || "").toLowerCase();
  if (p.endsWith(".ovpn")) return "openvpn";
  if (p.endsWith(".conf")) return "wireguard";
  return "";
}

// "\\server\share\folder", "//server/share", "smb://server/share" or "server/share" -> "smb://server/share/folder", or "".
export function smbUrl(address) {
  let a = String(address || "").trim();
  if (!a) return "";
  a = a.replace(/^smb:\/\//i, "").replace(/\\/g, "/").replace(/^\/+/, "");
  if (!/^[A-Za-z0-9._-]+(\/[^\/\s][^\/]*)*\/?$/.test(a) || a.includes("..")) return "";
  return "smb://" + a.replace(/\/$/, "");
}
