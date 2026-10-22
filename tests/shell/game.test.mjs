// Copied next to the built game.mjs by tests/shell/test_shell_build.py.
import test from "node:test";
import assert from "node:assert/strict";
import { isGame, activeGame, gigabytes, toastText } from "./game.mjs";

test("Steam, Proton, gamescope, Wine and launchers count as games", () => {
  for (const id of ["steam_app_1091500", "gamescope", "Cyberpunk2077.exe", "net.lutris.Lutris", "lutris",
                    "heroic", "org.libretro.RetroArch", "dolphin-emu", "Minecraft* 1.21", "STEAM_APP_570"])
    assert.ok(isGame(id), id);
});

test("ordinary apps are not games unless the person says so", () => {
  for (const id of ["kitty", "thunar", "steam", "org.gnome.Nautilus", "code", "", null, undefined])
    assert.equal(isGame(id), false, String(id));
  assert.ok(isGame("kitty", true));
  assert.ok(isGame("some-native-game", true));
  assert.equal(isGame("", true), false);
});

test("fullscreen video and browsers are never games", () => {
  for (const id of ["mpv", "io.mpv.Mpv", "vlc", "org.videolan.VLC", "celluloid", "io.github.celluloid_player.Celluloid",
                    "org.gnome.Totem", "org.kde.haruna", "kodi", "firefox", "org.mozilla.firefox", "chromium", "google-chrome"]) {
    assert.equal(isGame(id), false, id);
    assert.equal(isGame(id, true), false, id + " (treat fullscreen as game)");
  }
});

test("only a fullscreen game turns game mode on", () => {
  assert.equal(activeGame([]), "");
  assert.equal(activeGame(null), "");
  assert.equal(activeGame([{ appId: "steam_app_10", fullscreen: false }]), "");
  assert.equal(activeGame([{ appId: "mpv", fullscreen: true }]), "");
  assert.equal(activeGame([{ appId: "mpv", fullscreen: true }], true), "");
  assert.equal(activeGame([{ appId: "kitty", fullscreen: true }]), "");
  assert.equal(activeGame([{ appId: "kitty", fullscreen: true }], true), "kitty");
  assert.equal(activeGame([{ appId: "firefox", fullscreen: true }, { appId: "gamescope", fullscreen: true }]), "gamescope");
});

test("the toast states only what Core confirmed and measured", () => {
  assert.equal(toastText(true, 2.5 * 1024 ** 3), "Game mode: AI paused, GPU free (2.5 GB returned)");
  assert.equal(toastText(true, 14 * 1024 ** 3), "Game mode: AI paused, GPU free (14 GB returned)");
  assert.equal(toastText(true, null), "Game mode: AI paused, GPU free");
  assert.equal(toastText(true, 0), "Game mode: AI paused, GPU free");
  assert.equal(toastText(false, 3 * 1024 ** 3), "Game mode: AI paused");
  assert.equal(gigabytes(-1), "");
  assert.equal(gigabytes(NaN), "");
  assert.equal(gigabytes(512 * 1024 ** 2), "0.5 GB");
});
