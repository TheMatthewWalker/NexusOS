// Weather in the calendar (TopBar.qml), only when the person turns it on in
// Settings, Desktop with a place. Open-Meteo (open-meteo.com): no account or
// key; it is told only the place's coordinates. Tested with Node
// (tests/shell/weather.test.mjs).

export function geocodeUrl(name) {
  const q = String(name || "").trim().slice(0, 80);
  return q ? "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name=" + encodeURIComponent(q) : "";
}

// The first match -> { name, latitude, longitude } (rounded to ~1 km), or null.
export function parseGeocode(text) {
  let d;
  try { d = JSON.parse(text) } catch (e) { return null }
  const r = (d && d.results || [])[0];
  if (!r || typeof r.latitude !== "number" || typeof r.longitude !== "number") return null;
  const label = [r.name, r.admin1, r.country].filter(Boolean).filter((v, i, a) => a.indexOf(v) === i).join(", ");
  return { name: label, latitude: Math.round(r.latitude * 100) / 100, longitude: Math.round(r.longitude * 100) / 100 };
}

export function forecastUrl(place, fahrenheit) {
  if (!place || typeof place.latitude !== "number" || typeof place.longitude !== "number") return "";
  return "https://api.open-meteo.com/v1/forecast?latitude=" + place.latitude + "&longitude=" + place.longitude +
    "&current=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=4&timezone=auto" +
    (fahrenheit ? "&temperature_unit=fahrenheit" : "");
}

// WMO weather codes, in words.
export function describe(code) {
  const c = Number(code);
  if (c === 0) return "Clear";
  if (c <= 2) return "Partly cloudy";
  if (c === 3) return "Cloudy";
  if (c === 45 || c === 48) return "Fog";
  if (c >= 51 && c <= 57) return "Drizzle";
  if ((c >= 61 && c <= 67) || (c >= 80 && c <= 82)) return "Rain";
  if ((c >= 71 && c <= 77) || c === 85 || c === 86) return "Snow";
  if (c >= 95) return "Thunderstorms";
  return "";
}

// -> { now: { temp, words }, days: [{ date, high, low, words }] }, or null.
export function parseForecast(text) {
  let d;
  try { d = JSON.parse(text) } catch (e) { return null }
  if (!d || !d.current || typeof d.current.temperature_2m !== "number") return null;
  const daily = d.daily || {}, days = [];
  for (let i = 0; i < (daily.time || []).length && i < 4; i++)
    days.push({ date: daily.time[i], high: Math.round(daily.temperature_2m_max[i]), low: Math.round(daily.temperature_2m_min[i]),
                words: describe(daily.weather_code[i]) });
  return { now: { temp: Math.round(d.current.temperature_2m), words: describe(d.current.weather_code) }, days };
}
