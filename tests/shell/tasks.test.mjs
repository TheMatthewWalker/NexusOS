import { test } from "node:test";
import assert from "node:assert/strict";
import * as T from "./tasks.mjs";

const stat = (pid, name, ppid, ticks, rss) =>
  `${pid} (${name}) S ${ppid} 1 1 0 -1 4194304 0 0 0 0 ${ticks} 0 0 0 20 0 1 0 100 1000 ${rss} 0 0 0`;
function snap(ticks, idle) {
  return [
    stat(1, "systemd", 0, 50, 3000), stat(400, "firefox", 1, ticks.ff, 50000), stat(401, "Web Content (1)", 400, ticks.web, 80000),
    stat(500, "kitty", 1, 10, 9000), stat(600, "my build) x", 1, ticks.build, 1000),
    "===", "0 /proc/1", "1000 /proc/400", "1000 /proc/401", "1000 /proc/500", "1000 /proc/600",
    "===", `cpu  ${1000 + ticks.total} 0 0 ${idle} 0 0 0 0 0 0`,
    "===", "MemTotal:       16000000 kB", "MemAvailable:    6000000 kB", "SwapTotal:  4000000 kB", "SwapFree:  3000000 kB",
    "===", "4096", "100", "4"
  ].join("\n");
}
const a = T.parseSnapshot(snap({ ff: 100, web: 200, build: 0, total: 0 }, 9000));
const b = T.parseSnapshot(snap({ ff: 150, web: 300, build: 100, total: 400 }, 9400));
const u = T.usage(a, b, 2);

test("processes and the whole computer, from /proc", () => {
  assert.equal(b.procs[600].name, "my build) x");          // a name with a parenthesis
  assert.equal(b.procs[401].ppid, 400);
  assert.equal(b.procs[400].uid, 1000);
  assert.equal(Math.round(u.procs[400].cpu), 25);           // 50 ticks over 2 s at 100 ticks/s
  assert.equal(u.procs[400].memory, 50000 * 4096);
  assert.equal(Math.round(u.cpu), 50);                      // 400 busy of 800 ticks
  assert.equal(u.memoryUsed, 10000000 * 1024);
  assert.equal(u.swapUsed, 1000000 * 1024);
});

test("apps sum their child processes; background is the person's own", () => {
  const list = T.apps([{ pid: 400, class: "firefox", title: "News", address: "0x1" }, { pid: 400, class: "firefox", title: "Mail" },
                       { pid: 500, class: "kitty", title: "~" }, { pid: 1, class: "x" }], b, u);
  assert.deepEqual(list.map(x => x.name), ["Firefox", "Kitty"]);
  assert.deepEqual(list[0].pids.sort(), [400, 401]);
  assert.equal(Math.round(list[0].cpu), 75);
  assert.deepEqual(list[0].titles, ["News", "Mail"]);
  const bg = T.background(b, u, 1000, list.flatMap(x => x.pids));
  assert.deepEqual(bg.map(p => p.pid), [600]);
  assert.equal(T.canEnd(b, 600, 1000), true);
  assert.equal(T.canEnd(b, 1, 1000), false);               // not someone else's
  assert.equal(T.canEnd(b, 9999, 1000), false);
});

test("names and figures in words", () => {
  assert.equal(T.niceName("org.mozilla.firefox"), "Firefox");
  assert.equal(T.bytes(1.5e9), "1.5 GB");
  assert.equal(T.percent(3.14), "3.1%");
  assert.equal(T.percent(42.4), "42%");
});
