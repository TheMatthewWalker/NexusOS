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

// Sharing this computer's folders (nix/modules/sharing.nix): a Guardian change to turn on.
export const SHARING_PLAN = {
  description: "Let other computers on this network open folders you share (read-only)",
  changes: [{ option: "nexusos.sharing.enable", enabled: true }]
};

// /etc/nexus/firewall.json -> what is let in, in words. Known ports are named.
const KNOWN = [
  { tcp: [445, 139], udp: [], name: "Shared folders (Samba)" },
  { tcp: [5357], udp: [3702], name: "Showing in Windows' Network (WS-Discovery)" },
  { udp: [5353], tcp: [], name: "Finding printers and devices on this network (mDNS)" },
  { range: [1714, 1764], name: "Your phone (KDE Connect)" },
  { tcp: [22], udp: [], name: "Remote login (SSH)" }
];
export function firewall(text) {
  let f;
  try { f = JSON.parse(text) } catch (e) { return null }
  const tcp = new Set(f.allowedTCPPorts || []), udp = new Set(f.allowedUDPPorts || []);
  const ranges = [].concat(f.allowedTCPPortRanges || [], f.allowedUDPPortRanges || []);
  const open = [];
  for (const k of KNOWN) {
    if (k.range) {
      if (ranges.some(r => r.from === k.range[0] && r.to === k.range[1])) open.push(k.name);
    } else if (k.tcp.some(p => tcp.has(p)) || k.udp.some(p => udp.has(p))) {
      open.push(k.name);
      k.tcp.forEach(p => tcp.delete(p)); k.udp.forEach(p => udp.delete(p));
    }
  }
  const other = [...tcp].map(p => "TCP " + p).concat([...udp].map(p => "UDP " + p));
  return { on: !!f.enable, open, other };
}
