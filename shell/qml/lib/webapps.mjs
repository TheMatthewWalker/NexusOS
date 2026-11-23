// Apps, Web apps: websites in their own window (nexus-webapp; nix/modules/online.nix).
// Tested with Node (tests/shell/webapps.test.mjs). The address rule is the same
// as scripts/desktop/nexus_webapp.py's, which checks again.

export const presets = [
  { name: "Outlook", url: "https://outlook.office.com/mail/", summary: "Microsoft email and calendar" },
  { name: "Microsoft 365", url: "https://www.office.com/", summary: "Word, Excel and PowerPoint online" },
  { name: "Teams", url: "https://teams.microsoft.com/", summary: "Microsoft Teams chat and meetings" },
  { name: "Gmail", url: "https://mail.google.com/", summary: "Google email" },
  { name: "Google Docs", url: "https://docs.google.com/", summary: "Documents, sheets and slides" },
  { name: "Google Calendar", url: "https://calendar.google.com/", summary: "Google calendar" },
  { name: "WhatsApp", url: "https://web.whatsapp.com/", summary: "Messages from your phone" },
  { name: "YouTube", url: "https://www.youtube.com/", summary: "Videos" }
];

const URL_RE = /^https?:\/\/[A-Za-z0-9.-]+(?::\d{1,5})?(?:\/[A-Za-z0-9._~:/?#\[\]@!&()*+,;=%-]*)?$/;

// "outlook.office.com" -> https://outlook.office.com/ ; anything unsafe -> "".
export function normalise(text) {
  let t = String(text || "").trim();
  if (!t) return "";
  if (!/^[a-z]+:/i.test(t)) t = "https://" + t;
  if (/^https?:\/\/[^/]+$/i.test(t)) t += "/";
  return URL_RE.test(t) && t.length <= 500 ? t : "";
}

// A name from the address when none is given: "docs.google.com" -> "Docs".
export function nameFor(url) {
  const host = String(url || "").replace(/^https?:\/\//, "").split(/[/:]/)[0].replace(/^www\./, "");
  const first = host.split(".")[0] || "Web app";
  return first.charAt(0).toUpperCase() + first.slice(1);
}

export function added(list, url) { return (list || []).some(a => a.url === url) }
