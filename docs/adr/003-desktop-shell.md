# ADR-003: Desktop shell technology

**Status:** accepted (2026-10-22, by the owner). The evidence is in the
[SpaceUI shell spike](../design/spaceui-spike.md).

## Context

SpaceUI needs a shell that draws its windowless surfaces on Wayland:

- the plasma core;
- app emergence;
- the bottom launcher with open apps as planets;
- the chat bar;
- the orbital system panel.

It must sit below lock and secure surfaces and let clicks through its
transparent areas. It also has to integrate with the compositor and with
Nexus Core.

**The compositor is Hyprland** (confirmed by the owner, 21 Oct 2026). Hyprland
manages the windows: placement, tiling, snapping, workspaces and animations.
The shell is a separate program running on top of it, drawing the surfaces
above. This ADR chooses only that shell layer.

The two candidates were a native QML shell (Quickshell on Qt 6) and a web
shell (WebKitGTK in a layer-shell window with a small host). Both were built
with the same scope and shared drawing code, and were measured with the same
headless scenario.

## Decision

Build the SpaceUI shell natively with **Quickshell (QML, Qt 6)**.

1. Keep the drawing logic that is independent of the stack in
   `spike/spaceui/shared/` (to move to the shell's own package): the plasma
   shader body, the emergence and easing maths, and the generated tokens.
   QML imports ES modules directly, so this code stays testable with Node.
2. The shell runs on the `Top` layer, never `Overlay`, so lock and secure
   surfaces stay above it. Every surface sets an input mask.
3. Model output is rendered as plain text only.
4. The shell talks to Core through the authenticated local API. It never
   changes the system itself: system changes still go through Guardian.

## Consequences

- **Measured gains.** About half the memory (204 versus 427 MiB PSS), about
  3× faster startup, and lower CPU at the same 60 fps. Layer-shell, input
  masks and foreign-toplevel support are built in, and so is a Hyprland
  module.
- **Lost.** Direct reuse of the prototype's CSS. Styling is ported to QML by
  hand, guided by the shared tokens.
- **Still to verify.** Behaviour under Hyprland, in a NixOS VM test, which
  needs a render node. Real-GPU figures on the owner's hardware. A
  screen-reader pass.
- **If the web shell is chosen instead,** these must be in place:
  - the host must implement each system protocol itself, starting with
    foreign-toplevel;
  - gtk4-layer-shell must be preloaded, and the preload kept out of child
    processes;
  - WebKit's sandbox must stay enabled in production.
