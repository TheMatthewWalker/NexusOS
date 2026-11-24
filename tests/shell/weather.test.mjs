import { test } from "node:test";
import assert from "node:assert/strict";
import * as W from "./weather.mjs";

test("a place is looked up by name and kept to about a kilometre", () => {
  assert.equal(W.geocodeUrl(""), "");
  assert.match(W.geocodeUrl("São Paulo"), /name=S%C3%A3o%20Paulo$/);
  const p = W.parseGeocode(JSON.stringify({ results: [{ name: "Leeds", admin1: "England", country: "United Kingdom", latitude: 53.79648, longitude: -1.54785 }] }));
  assert.deepEqual(p, { name: "Leeds, England, United Kingdom", latitude: 53.8, longitude: -1.55 });
  assert.equal(W.parseGeocode("{}"), null);
  assert.equal(W.parseGeocode("not json"), null);
});

test("the forecast reads as words and whole degrees", () => {
  assert.equal(W.forecastUrl(null), "");
  assert.match(W.forecastUrl({ latitude: 53.8, longitude: -1.55 }, true), /latitude=53.8&longitude=-1.55.*temperature_unit=fahrenheit$/);
  const f = W.parseForecast(JSON.stringify({ current: { temperature_2m: 7.6, weather_code: 61 },
    daily: { time: ["2026-11-24", "2026-11-25"], weather_code: [3, 71], temperature_2m_max: [9.4, 3.2], temperature_2m_min: [4.1, -1.6] } }));
  assert.deepEqual(f.now, { temp: 8, words: "Rain" });
  assert.deepEqual(f.days[1], { date: "2026-11-25", high: 3, low: -2, words: "Snow" });
  assert.equal(W.parseForecast("{}"), null);
  assert.equal(W.describe(0), "Clear");
  assert.equal(W.describe(96), "Thunderstorms");
});
