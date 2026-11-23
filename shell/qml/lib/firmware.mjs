// Settings, Updates, Firmware (UpdatesPage.qml): fwupdmgr's JSON, read.
// Tested with Node (tests/shell/firmware.test.mjs).

function json(text) {
  try { const v = JSON.parse(String(text || "")); return v && typeof v === "object" ? v : {} } catch (e) { return {} }
}

// Release notes come as simple markup (<p>, <li>): plain text for a Text item.
export function plain(markup) {
  return String(markup || "").replace(/<li>/g, "• ").replace(/<\/(p|li)>/g, "\n").replace(/<[^>]*>/g, "")
    .replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/\n{2,}/g, "\n").trim();
}

// fwupdmgr get-updates --json: the devices that have a newer release, newest first.
export function updates(text) {
  return (json(text).Devices || []).filter(d => (d.Releases || []).length > 0 && /^[0-9a-f]{40}$/.test(d.DeviceId || ""))
    .map(d => {
      const r = d.Releases[0];
      return {
        id: d.DeviceId, name: d.Name || "A device", vendor: d.Vendor || "", current: d.Version || "", version: r.Version || "",
        summary: r.Summary || "", notes: plain(r.Description), urgent: ["high", "critical"].includes(r.Urgency),
        restart: (d.Flags || []).includes("needs-reboot") || (r.Flags || []).includes("needs-reboot") || (d.Flags || []).includes("internal")
      };
    });
}

// fwupdmgr get-devices --json: what fwupd can update, for "these are up to date".
export function devices(text) {
  return (json(text).Devices || []).filter(d => (d.Flags || []).includes("updatable"))
    .map(d => ({ name: d.Name || "A device", version: d.Version || "" }));
}

export function summary(list, checked) {
  if (!checked) return "Looking for firmware updates…";
  if (list.length === 0) return "Your computer's firmware is up to date.";
  return list.length === 1 ? "1 firmware update is ready." : list.length + " firmware updates are ready.";
}

export function line(u) {
  return (u.vendor ? u.vendor + " " : "") + u.name + ": " + (u.current ? u.current + " → " : "") + u.version +
    (u.urgent ? " · important" : "") + (u.restart ? " · finishes when you restart" : "");
}

// fwupdmgr's exit status: 2 is "nothing to do", not a failure.
export function result(code, err) {
  if (code === 0 || code === 2) return { ok: true, message: "" };
  if (/not authorized|authentication|dismissed/i.test(String(err))) return { ok: false, message: "The update was not allowed (the password was not given)." };
  if (/AC power|battery|plugged/i.test(String(err))) return { ok: false, message: "Plug in the charger first: firmware is only updated on mains power." };
  const last = String(err || "").trim().split("\n").filter(Boolean).pop();
  return { ok: false, message: "The update did not finish" + (last ? ": " + last : ".") };
}
