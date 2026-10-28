// The Wi-Fi network list in the system panel: which networks to show, in what
// order, what clicking one does, and what to say when connecting fails.
// Plain JavaScript so it runs in QML and is tested with Node
// (tests/shell/wifi.test.mjs). SystemPanel.qml maps Quickshell's enums to the
// strings used here.

// How a network is secured, as the panel needs to know it.
//   open      no password
//   password  a WPA/WPA2/WPA3 personal password (Quickshell: WpaPsk, Wpa2Psk, Sae)
//   enterprise  a username and password or certificate (WpaEap, Wpa2Eap, Suite B,
//             LEAP, dynamic WEP), which the panel cannot set up
//   unsupported  static WEP or unknown
export function securityKind(name) {
  switch (name) {
    case "Open": case "Owe": return "open";
    case "WpaPsk": case "Wpa2Psk": case "Sae": return "password";
    case "WpaEap": case "Wpa2Eap": case "Wpa3SuiteB192": case "Leap": case "DynamicWep": return "enterprise";
    default: return "unsupported";
  }
}

export function signalLabel(strength) {
  if (!(strength >= 0)) return "";
  if (strength >= 0.67) return "Strong";
  if (strength >= 0.34) return "Good";
  return "Weak";
}

// networks: [{ name, signal (0..1), security (securityKind), connected, known, state }]
// Hidden networks (no name) are left out. The connected network comes first,
// then saved ones, then by signal; each name appears once, at its best signal.
export function rows(networks, limit = 8) {
  const best = new Map();
  for (const n of networks || []) {
    if (!n || !n.name) continue;
    const seen = best.get(n.name);
    if (!seen || rank(n) < rank(seen) || (rank(n) === rank(seen) && (n.signal || 0) > (seen.signal || 0)))
      best.set(n.name, n);
  }
  return [...best.values()]
    .sort((a, b) => rank(a) - rank(b) || (b.signal || 0) - (a.signal || 0) || a.name.localeCompare(b.name))
    .slice(0, limit);
}
function rank(n) { return n.connected ? 0 : n.known ? 1 : 2; }

// What clicking a network does.
//   details   it is connected: show Disconnect and Forget
//   connect   open, or saved before: connect straight away
//   password  ask for the password first
//   explain   the panel cannot connect to it; say why
export function clickAction(n) {
  if (n.connected) return "details";
  if (n.security === "open" || n.known) return "connect";
  if (n.security === "password") return "password";
  return "explain";
}

export function explanation(n) {
  if (n.security === "enterprise")
    return n.name + " needs a username or certificate (work or school Wi-Fi). Set it up with nmtui in a terminal (Ctrl+Alt+T).";
  return n.name + " uses old or unknown security that NexusOS does not connect to from here. Use nmtui in a terminal (Ctrl+Alt+T).";
}

// WPA personal passwords are 8 to 63 characters, or 64 hexadecimal digits.
// WPA3 (SAE) allows shorter ones.
export function passwordProblem(password, securityName) {
  const p = password || "";
  if (p.length === 0) return "Type the network's password.";
  if (securityName === "Sae") return "";
  if (p.length === 64 && /^[0-9a-fA-F]+$/.test(p)) return "";
  if (p.length < 8) return "Wi-Fi passwords are at least 8 characters.";
  if (p.length > 63) return "Wi-Fi passwords are at most 63 characters.";
  return "";
}

// Quickshell's ConnectionFailReason, as a sentence.
export function failMessage(reason) {
  switch (reason) {
    case "NoSecrets": return "Wrong password, or the network asked for one. Try again.";
    case "WifiAuthTimeout": return "The network did not answer in time. Move closer to the router and try again.";
    case "WifiNetworkLost": return "The network went out of range.";
    case "WifiClientDisconnected": return "The network disconnected this computer.";
    case "WifiClientFailed": return "Could not connect. Check the password and try again.";
    default: return "Could not connect.";
  }
}

export function stateLabel(n) {
  if (n.connected) return "Connected";
  if (n.state === "Connecting") return "Connecting…";
  if (n.state === "Disconnecting") return "Disconnecting…";
  if (n.known) return "Saved";
  return "";
}
