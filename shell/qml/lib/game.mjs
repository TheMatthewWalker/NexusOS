// Game mode: which fullscreen window counts as a game, and what the toast says.
// Plain JavaScript so it runs in QML and is tested with Node
// (tests/shell/game.test.mjs). Matching is on the Wayland app id (the X11
// class under XWayland), case-insensitive.

// Games and game launchers. Proton titles report steam_app_<id>; Wine and
// Proton windows outside Steam often report the .exe name.
export const GAME_APP_IDS = [
  /^steam_app_\d+$/,
  /^gamescope$/,
  /\.exe$/,
  /^steam_proton$/,
  /^(net\.)?lutris(\.lutris)?$/,
  /^(com\.)?heroic(gameslauncher\.hgl)?$/,
  /^(com\.usebottles\.)?bottles$/,
  /^(org\.libretro\.)?retroarch$/,
  /^(org\.)?(dolphin-emu|ppsspp|pcsx2|rpcs3|ryujinx)$/,
  /^minecraft/,
];

// Fullscreen video and browsers are never games, even when the person has
// turned on "treat fullscreen as game".
export const NOT_GAME_APP_IDS = [
  /^(io\.mpv\.)?mpv$/,
  /^(org\.videolan\.)?vlc$/,
  /^(io\.github\.)?celluloid(_player\.celluloid)?$/,
  /^(org\.gnome\.)?totem$/,
  /^(org\.gnome\.)?showtime$/,
  /^(org\.kde\.)?(haruna|dragonplayer)$/,
  /^(com\.github\.rafostar\.)?clapper$/,
  /^(tv\.)?kodi$/,
  /^(org\.)?jellyfin/,
  /^(firefox|librewolf|chromium(-browser)?|google-chrome|brave-browser|microsoft-edge|org\.mozilla\.firefox)$/,
];

const matches = (list, appId) => list.some(re => re.test(appId));

// True when a window with this app id, in fullscreen, is a game.
export function isGame(appId, anyFullscreen = false) {
  const id = String(appId || "").trim().toLowerCase();
  if (!id || matches(NOT_GAME_APP_IDS, id)) return false;
  return anyFullscreen || matches(GAME_APP_IDS, id);
}

// The app id of the first fullscreen game among the open windows, or "".
// Windows are { appId, fullscreen } (Quickshell toplevels fit). A game left
// fullscreen on another workspace keeps game mode on while the person switches away.
export function activeGame(windows, anyFullscreen = false) {
  for (const w of windows || [])
    if (w && w.fullscreen && isGame(w.appId, anyFullscreen)) return String(w.appId);
  return "";
}

// "2.4 GB" from a byte count (binary gigabytes, as the GPU driver counts), or
// "" when the amount is unknown or nothing measurable was freed.
export function gigabytes(bytes) {
  if (typeof bytes !== "number" || !isFinite(bytes) || bytes <= 0) return "";
  const gb = bytes / 1024 ** 3;
  return (gb >= 10 ? gb.toFixed(0) : gb.toFixed(1)) + " GB";
}

// The toast text. "GPU free" is only said once Core confirmed the unload, and
// the amount only when telemetry measured it: nothing is guessed.
export function toastText(unloaded, freedBytes) {
  if (!unloaded) return "Game mode: AI paused";
  const amount = gigabytes(freedBytes);
  return "Game mode: AI paused, GPU free" + (amount ? ` (${amount} returned)` : "");
}
