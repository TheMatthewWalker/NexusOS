import { test } from "node:test";
import assert from "node:assert/strict";
import * as R from "./region.mjs";
import * as I from "./i18n.mjs";

test("region.env holds only valid locales and zones", () => {
  const env = R.envFile({ language: "fr_FR.UTF-8", formats: "de_DE.UTF-8", timeZone: "Europe/Paris" });
  assert.match(env, /^LANG=fr_FR.UTF-8\nLANGUAGE=fr_FR:fr:en\nLC_TIME=de_DE.UTF-8\n/);
  assert.match(env, /LC_PAPER=de_DE.UTF-8\nLC_MEASUREMENT=de_DE.UTF-8\nTZ=Europe\/Paris\n$/);
  assert.equal(R.envFile({ language: "en_GB.UTF-8" }), "LANG=en_GB.UTF-8\nLANGUAGE=en_GB:en\n");
  assert.equal(R.envFile({ language: "$(rm -rf ~)", formats: "x", timeZone: "../../etc/shadow" }), "");
  assert.equal(R.validZone("America/Argentina/Buenos_Aires"), true);
  assert.equal(R.validZone("UTC"), true);
  assert.equal(R.validZone("Europe/../x"), false);
  assert.deepEqual(R.parseEnv(env), { language: "fr_FR.UTF-8", formats: "de_DE.UTF-8", timeZone: "Europe/Paris", input: "" });
});

test("keyboard layouts for Hyprland, added and removed", () => {
  assert.equal(R.keyboard("gb", ""), null);
  assert.deepEqual(R.keyboard("gb", "fr,de"), { layout: "gb,fr,de", options: "grp:alt_shift_toggle" });
  assert.deepEqual(R.keyboard("gb", "gb,fr,;evil"), { layout: "gb,fr", options: "grp:alt_shift_toggle" });
  assert.equal(R.addLayout("", "fr", "gb"), "fr");
  assert.equal(R.addLayout("fr", "fr", "gb"), "fr");
  assert.equal(R.addLayout("fr", "gb", "gb"), "fr");
  assert.equal(R.addLayout("fr,de,es", "it", "gb"), "fr,de,es");
  assert.equal(R.removeLayout("fr,de", "fr"), "de");
});

test("time zones are searched by city", () => {
  const list = "Africa/Abidjan\nAmerica/New_York\nEurope/London\nEurope/Paris\nUTC\n";
  assert.deepEqual(R.zones(list, "new york"), ["America/New_York"]);
  assert.deepEqual(R.zones(list, "europe"), ["Europe/London", "Europe/Paris"]);
  assert.equal(R.zoneLabel("America/New_York"), "America / New York");
  assert.equal(R.localeName([{ locale: "fr_FR.UTF-8", name: "Français (France)" }], "fr_FR.UTF-8"), "Français (France)");
});

test("translations: the language is picked and missing texts stay English", () => {
  assert.equal(I.pick("fr_CA:fr:en", "fr_CA.UTF-8"), "fr");
  assert.equal(I.pick("", "de_AT.UTF-8"), "de");
  assert.equal(I.pick("en_GB:en", "en_GB.UTF-8"), "");
  assert.equal(I.pick("", "ja_JP.UTF-8"), "");
  assert.equal(I.pick("pt_BR:fr", ""), "fr");
  assert.equal(I.t({ Network: "Réseau" }, "Network"), "Réseau");
  assert.equal(I.t({ Network: "" }, "Network"), "Network");
  assert.equal(I.t({}, "Sound"), "Sound");
  assert.equal(I.t({ "{0} pages": "{0} Seiten" }, "{0} pages", [3]), "3 Seiten");
});

test("Chinese, Japanese and Korean typing follows the language unless chosen", () => {
  assert.equal(R.envFile({ language: "ja_JP.UTF-8", input: "none" }).includes("NEXUS_INPUT=none"), true);
  assert.equal(R.envFile({ language: "en_GB.UTF-8", input: "$(id)" }).includes("NEXUS_INPUT"), false);
  assert.equal(R.parseEnv("LANG=ko_KR.UTF-8\nNEXUS_INPUT=fcitx5\n").input, "fcitx5");
  assert.equal(R.parseEnv("NEXUS_INPUT=evil\n").input, "");
  assert.ok(R.inputMethodOn({ language: "zh_CN.UTF-8", input: "" }));
  assert.ok(!R.inputMethodOn({ language: "ja_JP.UTF-8", input: "none" }));
  assert.ok(R.inputMethodOn({ language: "en_GB.UTF-8", input: "fcitx5" }));
  assert.ok(!R.inputMethodOn({ language: "en_GB.UTF-8", input: "" }));
});
