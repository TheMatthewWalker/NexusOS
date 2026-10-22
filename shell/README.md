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

## The bridge

`nexus bridge` reads one JSON request per line on stdin and writes one JSON event per line on stdout. Each event is tagged with the id of the request it answers:

```
{"id":1,"op":"chat","prompt":"..."}          -> {"id":1,"type":"delta","text":"..."} … {"id":1,"type":"done","provider":"local"}
{"id":2,"op":"suggest","prompt":"...","model":false}  -> {"id":2,"type":"suggestions","data":{"apps":[…],"source":"rules"}}
{"id":3,"op":"apps"} | {"id":4,"op":"health"}
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
