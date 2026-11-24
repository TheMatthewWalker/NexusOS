import { test } from "node:test";
import assert from "node:assert/strict";
import * as C from "./calendar.mjs";

test("a month is six weeks from the locale's first day", () => {
  // November 2026 starts on a Sunday.
  const mon = C.monthGrid(2026, 10, 1);
  assert.equal(mon.length, 42);
  assert.deepEqual(mon[0], { year: 2026, month: 9, day: 26, inMonth: false });
  assert.deepEqual(mon[6], { year: 2026, month: 10, day: 1, inMonth: true });
  const sun = C.monthGrid(2026, 10, 0);
  assert.deepEqual(sun[0], { year: 2026, month: 10, day: 1, inMonth: true });
  assert.equal(sun.filter(c => c.inMonth).length, 30);
  assert.deepEqual(C.monthGrid(2024, 1, 1).filter(c => c.inMonth).length, 29);   // leap year
});

test("headers, months either side, today and week numbers", () => {
  const names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  assert.deepEqual(C.weekdayHeaders(names, 1), ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]);
  assert.deepEqual(C.weekdayHeaders(names, 0), names);
  assert.deepEqual(C.shift(2026, 11, 1), { year: 2027, month: 0 });
  assert.deepEqual(C.shift(2026, 0, -1), { year: 2025, month: 11 });
  assert.ok(C.isSameDay({ year: 2026, month: 10, day: 24 }, new Date(2026, 10, 24, 15, 0)));
  assert.equal(C.isoWeek(2026, 10, 24), 48);
  assert.equal(C.isoWeek(2021, 0, 3), 53);
  assert.equal(C.isoWeek(2026, 0, 1), 1);
});
