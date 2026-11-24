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

// Proxy (Settings, Network): GNOME's proxy settings, which Firefox and GTK
// apps follow. "host:port" (or http://host:port) -> { host, port }, or null.
export function proxyAddress(text) {
  const m = /^(?:https?:\/\/)?([A-Za-z0-9.-]{1,253}|\[[0-9A-Fa-f:]+\]):(\d{1,5})\/?$/.exec(String(text || "").trim());
  if (!m || Number(m[2]) < 1 || Number(m[2]) > 65535) return null;
  return { host: m[1], port: Number(m[2]) };
}
// A PAC address: http(s) only.
export function pacUrl(text) {
  const t = String(text || "").trim();
  return /^https?:\/\/[^\s'"\\]+$/.test(t) && t.length <= 2000 ? t : null;
}
// GVariant text for dconf write: a string in single quotes.
function gstr(v) { return "'" + String(v).replace(/\\/g, "\\\\").replace(/'/g, "\\'") + "'" }
// The dconf writes for a proxy choice: [key, value] pairs, or null when the address is not valid.
export function proxyWrites(mode, text) {
  const base = "/org/gnome/system/proxy/";
  if (mode === "none") return [[base + "mode", gstr("none")]];
  if (mode === "auto") {
    const url = pacUrl(text);
    return url ? [[base + "autoconfig-url", gstr(url)], [base + "mode", gstr("auto")]] : null;
  }
  if (mode === "manual") {
    const a = proxyAddress(text);
    if (!a) return null;
    const w = [];
    for (const kind of ["http", "https"]) w.push([base + kind + "/host", gstr(a.host)], [base + kind + "/port", String(a.port)]);
    w.push([base + "ignore-hosts", "['localhost', '127.0.0.0/8', '::1']"], [base + "mode", gstr("manual")]);
    return w;
  }
  return null;
}
// `dconf read` of mode, autoconfig-url, http/host and http/port (one per line) -> { mode, text }.
export function proxyNow(text) {
  const [mode, url, host, port] = String(text || "").split("\n").map(l => l.trim().replace(/^'|'$/g, ""));
  if (mode === "auto") return { mode, text: url || "" };
  if (mode === "manual") return { mode, text: host ? host + ":" + (port || "") : "" };
  return { mode: "none", text: "" };
}
