import { test } from "node:test";
import assert from "node:assert/strict";
import * as N from "./network.mjs";

test("VPNs from nmcli, other connections left out", () => {
  const v = N.vpns([
    "Home Wi-Fi:6f1d1b4a-0a51-4c7a-9a2e-2a3b4c5d6e7f:802-11-wireless:yes",
    "Work\\: London:7f1d1b4a-0a51-4c7a-9a2e-2a3b4c5d6e7f:vpn:no",
    "wg0:8f1d1b4a-0a51-4c7a-9a2e-2a3b4c5d6e7f:wireguard:yes",
    "bad:not-a-uuid:vpn:no"
  ].join("\n"));
  assert.deepEqual(v.map(x => [x.name, x.kind, x.active]), [["Work: London", "VPN", false], ["wg0", "WireGuard", true]]);
  assert.equal(N.validUuid("--evil"), false);
});

test("VPN files and shared-folder addresses", () => {
  assert.equal(N.importType("/home/a/Downloads/office.ovpn"), "openvpn");
  assert.equal(N.importType("wg0.CONF"), "wireguard");
  assert.equal(N.importType("notes.txt"), "");
  assert.equal(N.smbUrl("\\\\nas\\Photos\\2026"), "smb://nas/Photos/2026");
  assert.equal(N.smbUrl("smb://192.168.1.10/share/"), "smb://192.168.1.10/share");
  assert.equal(N.smbUrl("//server/share"), "smb://server/share");
  assert.equal(N.smbUrl("server; rm -rf"), "");
  assert.equal(N.smbUrl("nas/../etc"), "");
  assert.equal(N.smbUrl(""), "");
});

test("sharing this computer's folders is a Guardian change", () => {
  assert.deepEqual(N.SHARING_PLAN.changes, [{ option: "nexusos.sharing.enable", enabled: true }]);
});

test("the firewall's openings are named in words", () => {
  const f = N.firewall(JSON.stringify({ enable: true, allowedTCPPorts: [445, 139, 8080], allowedUDPPorts: [5353],
                                        allowedTCPPortRanges: [{ from: 1714, to: 1764 }], allowedUDPPortRanges: [] }));
  assert.equal(f.on, true);
  assert.deepEqual(f.open, ["Shared folders (Samba)", "Finding printers and devices on this network (mDNS)", "Your phone (KDE Connect)"]);
  assert.deepEqual(f.other, ["TCP 8080"]);
  assert.equal(N.firewall("nope"), null);
});

test("proxy settings are checked and written as dconf values", () => {
  assert.deepEqual(N.proxyAddress("proxy.example.com:8080"), { host: "proxy.example.com", port: 8080 });
  assert.deepEqual(N.proxyAddress("http://10.0.0.1:3128/"), { host: "10.0.0.1", port: 3128 });
  for (const bad of ["proxy", "proxy:0", "proxy:70000", "a b:80", "x';rm -rf ~:80", ""]) assert.equal(N.proxyAddress(bad), null, bad);
  assert.equal(N.pacUrl("http://wpad/proxy.pac"), "http://wpad/proxy.pac");
  assert.equal(N.pacUrl("file:///etc/passwd"), null);
  assert.equal(N.pacUrl("http://x/'y"), null);
  assert.deepEqual(N.proxyWrites("none", ""), [["/org/gnome/system/proxy/mode", "'none'"]]);
  const m = N.proxyWrites("manual", "proxy.example.com:8080");
  assert.deepEqual(m.slice(0, 2), [["/org/gnome/system/proxy/http/host", "'proxy.example.com'"], ["/org/gnome/system/proxy/http/port", "8080"]]);
  assert.deepEqual(m[m.length - 1], ["/org/gnome/system/proxy/mode", "'manual'"]);
  assert.equal(N.proxyWrites("manual", "nope"), null);
  assert.deepEqual(N.proxyWrites("auto", "http://wpad/proxy.pac")[1], ["/org/gnome/system/proxy/mode", "'auto'"]);
  assert.deepEqual(N.proxyNow("'manual'\n''\n'proxy.example.com'\n8080\n"), { mode: "manual", text: "proxy.example.com:8080" });
  assert.deepEqual(N.proxyNow("'auto'\n'http://wpad/proxy.pac'\n\n\n"), { mode: "auto", text: "http://wpad/proxy.pac" });
  assert.deepEqual(N.proxyNow(""), { mode: "none", text: "" });
});

test("a Broadcom Wi-Fi chip with no driver is found, and one that has one is told apart", () => {
  const none = "00:02.0 VGA compatible controller [0300]: Intel Corporation HD Graphics [8086:0166] (rev 09)\n\tKernel driver in use: i915\n" +
               "03:00.0 Network controller [0280]: Broadcom Inc. and subsidiaries BCM4360 802.11ac Wireless Network Adapter [14e4:43a0] (rev 03)\n" +
               "\tSubsystem: Apple Inc. Device [106b:0117]\n\tKernel modules: bcma\n";
  assert.deepEqual(N.broadcomWifi(none), { name: "BCM4360", inUse: "" });
  assert.deepEqual(N.broadcomWifi(none.replace("Kernel modules: bcma", "Kernel driver in use: wl")), { name: "BCM4360", inUse: "wl" });
  assert.equal(N.broadcomWifi("03:00.0 Network controller [0280]: Intel Corporation Wi-Fi 6 AX200 [8086:2723]\n\tKernel driver in use: iwlwifi\n"), null);
  assert.equal(N.broadcomWifi("02:00.0 Ethernet controller [0200]: Broadcom Inc. NetXtreme BCM5762 [14e4:1687]\n"), null);
  assert.deepEqual(N.BROADCOM_PLAN.changes, [{ option: "nexusos.hardware.broadcomWifi.enable", enabled: true }]);
});
