// The month calendar that opens from the clock in the top bar (TopBar.qml),
// as on Windows. Tested with Node (tests/shell/calendar.test.mjs).

// Six weeks of seven days covering `month` (0-11) of `year`, starting on
// `firstDay` (0 Sunday ... 6 Saturday, from the locale). Each day is
// { year, month, day, inMonth }.
export function monthGrid(year, month, firstDay) {
  const first = new Date(year, month, 1);
  const lead = (first.getDay() - (Number(firstDay) || 0) + 7) % 7;
  const out = [];
  for (let i = 0; i < 42; i++) {
    const d = new Date(year, month, 1 - lead + i);
    out.push({ year: d.getFullYear(), month: d.getMonth(), day: d.getDate(), inMonth: d.getMonth() === month });
  }
  return out;
}

// The short day names in grid order, from names[0] = Sunday.
export function weekdayHeaders(names, firstDay) {
  const out = [];
  for (let i = 0; i < 7; i++) out.push(names[(i + (Number(firstDay) || 0)) % 7]);
  return out;
}

// One month earlier (-1) or later (+1): { year, month }.
export function shift(year, month, by) {
  const m = month + by;
  return { year: year + Math.floor(m / 12), month: ((m % 12) + 12) % 12 };
}

export function isSameDay(cell, date) {
  return cell.year === date.getFullYear() && cell.month === date.getMonth() && cell.day === date.getDate();
}

// ISO 8601 week number (Monday weeks, as in most of Europe) of a date.
export function isoWeek(year, month, day) {
  const d = new Date(Date.UTC(year, month, day));
  const dow = d.getUTCDay() || 7;
  d.setUTCDate(d.getUTCDate() + 4 - dow);
  const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
  return Math.ceil(((d - yearStart) / 86400000 + 1) / 7);
}
