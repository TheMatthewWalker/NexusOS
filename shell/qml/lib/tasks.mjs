// Task manager (Tasks.qml): what is running and how much it uses, from /proc
// and Hyprland's window list. Tested with Node (tests/shell/tasks.test.mjs).
//
// One snapshot is the output of:
//   cat /proc/[0-9]*/stat; echo ===; stat -c '%u %n' /proc/[0-9]*; echo ===;
//   head -1 /proc/stat; echo ===; cat /proc/meminfo; echo ===; getconf PAGESIZE; getconf CLK_TCK; nproc

export const SEP = "===";

// "/proc/PID/stat": pid (comm) state ppid ... utime(14) stime(15) ... rss(24, pages).
// comm can hold spaces and parentheses, so it is read up to the last ")".
export function parseStat(line) {
  const open = line.indexOf("("), close = line.lastIndexOf(")");
  if (open < 0 || close < open) return null;
  const pid = parseInt(line.slice(0, open));
  const rest = line.slice(close + 2).split(" ");
  // rest[0] is field 3 (state); field n is rest[n - 3].
  const f = n => rest[n - 3];
  if (isNaN(pid) || rest.length < 22) return null;
  return { pid, name: line.slice(open + 1, close), state: f(3), ppid: parseInt(f(4)),
           ticks: parseInt(f(14)) + parseInt(f(15)), rssPages: parseInt(f(24)) };
}

export function parseSnapshot(text) {
  const parts = String(text || "").split("\n" + SEP + "\n");
  const procs = {};
  for (const line of (parts[0] || "").split("\n")) { const p = parseStat(line); if (p) procs[p.pid] = p }
  for (const line of (parts[1] || "").split("\n")) {
    const m = /^(\d+) \/proc\/(\d+)$/.exec(line.trim());
    if (m && procs[m[2]]) procs[m[2]].uid = parseInt(m[1]);
  }
  const cpu = (parts[2] || "").trim().split(/\s+/).slice(1).map(Number);
  const mem = {};
  for (const line of (parts[3] || "").split("\n")) { const m = /^(\w+):\s+(\d+)/.exec(line); if (m) mem[m[1]] = parseInt(m[2]) * 1024 }
  const conf = (parts[4] || "").trim().split(/\s+/);
  return { procs, cpuTotal: cpu.reduce((a, b) => a + (b || 0), 0), cpuIdle: (cpu[3] || 0) + (cpu[4] || 0), mem,
           pageSize: parseInt(conf[0]) || 4096, ticksPerSecond: parseInt(conf[1]) || 100, cores: parseInt(conf[2]) || 1 };
}

// Usage between two snapshots: each process's share of one core (100% = one core
// fully used, as `top` shows it), and the whole computer's.
export function usage(prev, cur, seconds) {
  const out = {};
  const ticks = cur.ticksPerSecond * Math.max(0.1, seconds);
  for (const pid in cur.procs) {
    const p = cur.procs[pid], q = prev && prev.procs[pid];
    out[pid] = { cpu: q ? Math.max(0, (p.ticks - q.ticks) / ticks * 100) : 0, memory: p.rssPages * cur.pageSize };
  }
  const total = prev ? cur.cpuTotal - prev.cpuTotal : 0, idle = prev ? cur.cpuIdle - prev.cpuIdle : 0;
  const m = cur.mem;
  return { procs: out,
           cpu: total > 0 ? Math.max(0, Math.min(100, (1 - idle / total) * 100)) : 0,
           memoryUsed: (m.MemTotal || 0) - (m.MemAvailable || 0), memoryTotal: m.MemTotal || 0,
           swapUsed: (m.SwapTotal || 0) - (m.SwapFree || 0), swapTotal: m.SwapTotal || 0 };
}

function children(procs) {
  const kids = {};
  for (const pid in procs) (kids[procs[pid].ppid] = kids[procs[pid].ppid] || []).push(parseInt(pid));
  return kids;
}
export function tree(procs, root) {
  const kids = children(procs), out = [], todo = [root];
  while (todo.length) { const p = todo.pop(); if (!procs[p] || out.includes(p)) continue; out.push(p); todo.push(...(kids[p] || [])) }
  return out;
}

// Apps: one per window's process (hyprctl clients -j: pid, class, title), with
// everything it started, summed. Several windows of one process are one app.
export function apps(clients, snap, use) {
  const seen = {};
  for (const c of clients || []) {
    if (!c || !c.pid || c.pid < 2 || !snap.procs[c.pid]) continue;
    const a = seen[c.pid] || (seen[c.pid] = { pid: c.pid, name: niceName(c.class || snap.procs[c.pid].name), titles: [], addresses: [] });
    if (c.title) a.titles.push(c.title);
    if (c.address) a.addresses.push(c.address);
  }
  return Object.values(seen).map(a => {
    const pids = tree(snap.procs, a.pid);
    const cpu = pids.reduce((s, p) => s + ((use.procs[p] || {}).cpu || 0), 0);
    const memory = pids.reduce((s, p) => s + ((use.procs[p] || {}).memory || 0), 0);
    return Object.assign({}, a, { pids, cpu, memory, title: a.titles[0] || "" });
  }).sort((x, y) => y.cpu - x.cpu || y.memory - x.memory);
}

// The person's other processes (not part of an app's window), busiest first.
export function background(snap, use, uid, appPids, limit) {
  const inApp = new Set(appPids);
  return Object.values(snap.procs).filter(p => p.uid === uid && !inApp.has(p.pid) && p.state !== "Z")
    .map(p => ({ pid: p.pid, name: p.name, cpu: (use.procs[p.pid] || {}).cpu || 0, memory: (use.procs[p.pid] || {}).memory || 0 }))
    .sort((x, y) => y.cpu - x.cpu || y.memory - x.memory).slice(0, limit || 40);
}

// "org.mozilla.firefox" -> "Firefox", "code-oss" -> "Code-oss", "kitty" -> "Kitty".
export function niceName(id) {
  const last = String(id || "").split(".").pop() || "App";
  return last.charAt(0).toUpperCase() + last.slice(1);
}

export function bytes(b) {
  const n = Number(b) || 0;
  if (n >= 1e9) return (n / 1e9).toFixed(1) + " GB";
  if (n >= 1e6) return Math.round(n / 1e6) + " MB";
  return Math.round(n / 1e3) + " KB";
}
export function percent(v) { return (v >= 10 ? Math.round(v) : Math.round(v * 10) / 10) + "%" }

// Only processes of this person are ended from here (pids checked as numbers).
export function canEnd(snap, pid, uid) { const p = snap.procs[pid]; return !!p && p.uid === uid && pid > 1 }
