# Nexus shell

The NexusOS desktop interface (SpaceUI), written in [Quickshell](https://quickshell.org)
(QML on Qt 6) and running on Hyprland. The decisions are recorded in
[ADR-003](../docs/adr/003-desktop-shell.md) and the
[design pack](../docs/design/spaceui/README.md).

## What it draws

| Part | File | Notes |
|---|---|---|
| Deep-space wallpaper | `qml/Wallpaper.qml` | Background layer; painted once, so it costs nothing while idle |
| Bottom launcher | `qml/shell.qml` | A small Nova core. Open windows orbit it as tiny planets, which spread into a labelled orbit after a 600 ms hover |
| Full Nexus core | `qml/shell.qml`, `qml/NovaCore.qml` | The approved 3D Nova core, the app orbit, the chat bar and the streamed reply |
| System panel | `qml/SystemPanel.qml` | Top-right orbit of system planets, with quick volume, Wi-Fi, Bluetooth and mute controls |
| Core link | `qml/CoreBridge.qml` | One long-lived `nexus bridge` process (see below) |
| App roles | `qml/AppDirectory.qml` | Maps the six orbit roles to installed desktop entries |

Super+Space opens and closes Nexus. You can also run
`nexus-shell-ipc call nexus toggle`.

## Rules the shell keeps

- **The AI only suggests.** Suggestions come from Nexus Core (`POST /suggest`). An app opens only when you pick it.
- **Nothing is faked.**
  - If Core is offline, the shell says so and apps still open.
  - A role with no installed app shows "Not installed".
  - A missing audio, Wi-Fi, Bluetooth or battery service shows as unavailable.
- **Locks stay on top.** Every surface sits on the `Top` layer, never `Overlay`, so lock screens cover it. The VM test checks this.
- **Plain text only.** Model output is rendered as plain text.
- **No token in the shell.** The shell never holds the Core API token. It talks to Core through the `nexus bridge` command, which authenticates like any CLI call.

## Sharing the GPU with the model

The local model and the Nova core share the GPU, and the core gives way:

| When | What the core does |
|---|---|
| The model is thinking or answering | The big core draws at most 30 frames per second, with at most 32 samples per pixel (`busyFps`, `busySteps` in `NovaCore.qml`) |
| The screen is idle for 60 s, or a window is fullscreen (a game, a video) | The launcher core stops drawing and keeps its last frame |
| Reduced motion is on | No animation at all |
| Nexus is closed | The big core is not drawn |
| A game is fullscreen (game mode) | The model is unloaded, the launcher core stops, and Hyprland blur and animations are off until the game closes |

### Game mode

A fullscreen window counts as a game when its app id matches the list in
`qml/lib/game.mjs`: Steam and Proton titles (`steam_app_*`), gamescope, Wine
(`*.exe`), common launchers and emulators. Video players and browsers never
count. With "treat fullscreen as game" on (`gameMode.anyFullscreen`, or
`nexus-shell-ipc call nexus fullscreenIsGame true`) any other fullscreen window
counts too. A game left fullscreen on another workspace keeps game mode on.

When game mode starts, the shell:
- asks Core to unload the model (bridge op `unload`), which reports the GPU memory Core's telemetry saw come back;
- runs `hyprctl --batch "keyword decoration:blur:enabled false ; keyword animations:enabled false"`;
- shows a Hyprland notification, "Game mode: AI paused, GPU free (2.4 GB returned)". The amount appears only when it was measured (NVIDIA today), and "GPU free" only when Core confirmed the unload. Hyprland draws fullscreen windows above the shell's Top layer, so a compositor notification is the only toast that shows over the game without using the Overlay layer.

When the game closes, blur and animations go back to the configured values. The
model is not preloaded; it loads on the next question. Turn the whole feature
off with `services.nexus-desktop.gameMode.enable = false;`.

The core needs well under 1 MB of graphics memory (one 528×528 noise texture).
`scripts/bench/nexus_core_gpu_bench.py` measures what it costs on a real machine
([M5 evidence](../docs/evidence/M5.md)).

## The bridge

`nexus bridge` reads one JSON request per line on stdin and writes one JSON event per line on stdout. Each event is tagged with the id of the request it answers:

```
{"id":1,"op":"chat","prompt":"..."}          -> {"id":1,"type":"delta","text":"..."} … {"id":1,"type":"done","provider":"local"}
{"id":2,"op":"suggest","prompt":"...","model":false}  -> {"id":2,"type":"suggestions","data":{"apps":[…],"source":"rules"}}
{"id":3,"op":"apps"} | {"id":4,"op":"health"}
{"id":5,"op":"unload"}                       -> {"id":5,"type":"unloaded","data":{"vramFreedBytes":2576980378}}  (null when not measured)
```

## Build and run

The NixOS module `nixosModules.desktop` installs everything. Set `services.nexus-desktop.enable = true;`; the options are in `nix/modules/desktop.nix`.

For development:

```sh
nix develop .#desktop --command python3 shell/tools/build.py --out shell/build
quickshell -p shell/build/shell.qml          # inside a running Hyprland session
nix develop .#desktop --command python3 -m unittest tests/shell/test_shell_build.py
nix build .#checks.x86_64-linux.hyprland-desktop   # the whole desktop in a VM
```

`shell/tools/build.py` copies `qml/` and generates, under `lib/`:
- the design tokens;
- the Qt-wrapped Nova shader;
- the noise atlas.

Edit `qml/lib/nova-core.glsl`, never the generated `nova.frag`.

The colours in `shell.qml` (`root.dark`) are a provisional dark theme until a dark token set is agreed.
