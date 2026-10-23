// Plain-language system state (feature 3): one short sentence per system planet,
// built only from real readings. Plain JavaScript so it runs in QML and is
// tested with Node. Nothing here invents data: a reading that is unknown
// (null, undefined, NaN, or a non-positive estimate) is left out of the
// sentence, and a missing service says so (binding rule 2).

const known = v => typeof v === "number" && Number.isFinite(v);
const pct = fraction => Math.round(fraction * 100) + "%";

// 11400 -> "3 h 10 min", 2400 -> "40 min", 7200 -> "2 h", 20 -> "less than a minute".
export function duration(seconds) {
  if (!known(seconds) || seconds <= 0) return "";
  const minutes = Math.round(seconds / 60);
  if (minutes < 1) return "less than a minute";
  const h = Math.floor(minutes / 60), m = minutes % 60;
  if (h === 0) return `${m} min`;
  return m === 0 ? `${h} h` : `${h} h ${m} min`;
}

// Decimal units, as file managers show them. 1.5e9 -> "1.5 GB", 4.2e10 -> "42 GB".
export function bytes(n) {
  if (!known(n) || n < 0) return "";
  const units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
  let i = 0, v = n;
  while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
  if (i === 0) return `${Math.round(v)} bytes`;
  return (v < 10 ? (Math.round(v * 10) / 10).toString() : Math.round(v).toString()) + " " + units[i];
}

// Battery, from UPower's display device. state is one of "charging",
// "discharging", "full", "empty", "pending-charge", "pending-discharge" or
// "unknown". Times are UPower's own estimates in seconds (0 when it has none).
export function power({ available, present, state, percentage, timeToEmpty, timeToFull } = {}) {
  if (!available) return "Power information unavailable";
  if (!present) return "Plugged in, no battery";
  const level = known(percentage) ? ` (${pct(percentage)})` : "";
  switch (state) {
    case "full": return "Fully charged";
    case "charging": {
      const t = duration(timeToFull);
      return (t ? `Charging, full in about ${t}` : "Charging") + level;
    }
    case "discharging": {
      const t = duration(timeToEmpty);
      return (t ? `About ${t} left` : "On battery") + level;
    }
    case "empty": return "Battery empty, plug in now";
    case "pending-charge": return "Plugged in, not charging" + level;
    case "pending-discharge": return "On battery" + level;
    default: return known(percentage) ? `Battery at ${pct(percentage)}` : "Battery state unknown";
  }
}

// Default audio output, from PipeWire.
export function sound({ available, muted, volume, device } = {}) {
  if (!available) return "No sound output found";
  if (muted) return "Muted";
  const through = device ? ` through ${device}` : "";
  const level = known(volume) ? ` at ${pct(volume)}` : "";
  return `Playing${through}${level}`;
}

// Networking. connectivity is NetworkManager's check: "full", "limited",
// "portal", "none" or "unknown" (unknown adds nothing to the sentence).
export function network({ available, wifiHardware, wifiEnabled, ssid, wired, connectivity } = {}) {
  if (!available) return "Network status unavailable";
  let base;
  if (ssid) base = `Connected to ${ssid}`;
  else if (wired) base = "Connected by cable";
  else if (wifiHardware && !wifiEnabled) return "Wi-Fi is off";
  else return "No network";
  if (connectivity === "limited" || connectivity === "none") return base + ", but the internet is not reachable";
  if (connectivity === "portal") return base + "; this network wants you to sign in";
  return base;
}

// Bluetooth adapter power and connected devices (connected null = not known).
export function bluetooth({ available, enabled, connected } = {}) {
  if (!available) return "No Bluetooth adapter";
  if (!enabled) return "Off";
  if (!known(connected)) return "On";
  if (connected === 0) return "On, nothing connected";
  return `On, ${connected} ${connected === 1 ? "device" : "devices"} connected`;
}

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const MONTHS = ["January", "February", "March", "April", "May", "June", "July",
                "August", "September", "October", "November", "December"];

// UTC offset in minutes east (the opposite sign of Date#getTimezoneOffset).
export function utcOffset(minutesEast) {
  if (!known(minutesEast)) return "";
  if (minutesEast === 0) return "UTC";
  const sign = minutesEast > 0 ? "+" : "-", a = Math.abs(minutesEast);
  const h = Math.floor(a / 60), m = a % 60;
  return `UTC${sign}${h}` + (m ? ":" + String(m).padStart(2, "0") : "");
}

// Local date and time, with the zone name when known and the UTC offset as a hint.
export function time(date, zone) {
  if (!(date instanceof Date) || Number.isNaN(date.getTime())) return "Time unavailable";
  const hh = String(date.getHours()).padStart(2, "0"), mm = String(date.getMinutes()).padStart(2, "0");
  const offset = utcOffset(-date.getTimezoneOffset());
  const hint = zone && zone !== offset ? `${zone}, ${offset}` : offset;
  return `${DAYS[date.getDay()]} ${date.getDate()} ${MONTHS[date.getMonth()]}, ${hh}:${mm} (${hint})`;
}

// Parse `df -P -B1 <paths>`: one volume per distinct filesystem, in input order.
// Unreadable lines (and paths df could not read) are skipped.
export function parseDf(text) {
  const out = [], seen = new Set();
  for (const line of String(text || "").split("\n").slice(1)) {
    const m = line.match(/^(\S+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+|-)%?\s+(.+)$/);
    if (!m) continue;
    const [, source, size, used, free, , mount] = m;
    if (seen.has(source)) continue;
    seen.add(source);
    out.push({ source, mount: mount.trim(), size: Number(size), used: Number(used), free: Number(free) });
  }
  return out;
}

const LOW_SPACE = 0.10;
// Measured like df's Capacity column: blocks reserved for root do not count.
const isLow = v => v.used + v.free > 0 && v.free / (v.used + v.free) < LOW_SPACE;
const volumeName = mount => mount === "/" ? "System" : mount === "/home" ? "Home" : mount;

// True when any volume has under 10% free: the panel then offers "Show biggest files".
export function diskLow(volumes) {
  return Array.isArray(volumes) && volumes.some(isLow);
}

// "40 GB free of 500 GB", or per volume when / and /home are separate.
export function disk(volumes) {
  if (!Array.isArray(volumes) || volumes.length === 0) return "Storage information unavailable";
  const parts = volumes.map(v => {
    const free = `${bytes(v.free)} free of ${bytes(v.size)}`;
    return volumes.length === 1 ? free : `${volumeName(v.mount)}: ${free}`;
  });
  const low = volumes.filter(isLow).map(v => volumeName(v.mount));
  let text = parts.join("; ");
  if (low.length) text += volumes.length === 1 ? ". Space is running low" : `. ${low.join(" and ")} is running low on space`;
  return text;
}

// Screens attached, from Quickshell.screens.
export function display(count) {
  if (!known(count) || count < 1) return "No screen information";
  return count === 1 ? "1 screen connected" : `${count} screens connected`;
}
