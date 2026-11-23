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
