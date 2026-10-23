# SpaceUI design pack

SpaceUI is the NexusOS desktop interface. The design was developed in a separate
browser-prototype project (React, prototype commit `bc1c32f608cbda66703fcaf706835c38a8388dbc`).
This folder archives the working documents handed over from that project, so the
design lives next to the code that implements it.

Start with [agent-start-here.md](agent-start-here.md). It is the handover brief and
carries the binding rules. Then read [design-tokens.json](design-tokens.json),
[core-study.md](core-study.md) and [prototype-TODO.md](prototype-TODO.md).

## Decisions

| Date | Decision | Status |
|---|---|---|
| 21 Oct 2026 | **Nexus core identity: the plasma core** (`assets/nexus-plasma-original.png`, see `screens/core-study-plasma-approved.webp`). It replaces the soft pearl as the approved core. It keeps the core study's motion rules: calm idle drift, waking on hover or focus, the gases reacting while responding, a still image under reduced motion, and a still-image fallback if rendering fails | **Superseded** by Nova (22 Oct) |
| 22 Oct 2026 | **Nexus core: "Nova", a live 3D core** ([study](studies/nexus-core-nova.html), `screens/core-study-nova-*.webp`). It is a raymarched volume with a white-hot heart that beats (54 bpm idle, faster when listening or thinking), a lens flare, amber tendrils, a lacy fiery filament shell, blue-violet outer gas, thin orbiting rings and star dust. It turns when dragged and reacts to idle, listening, thinking and speaking. It **supersedes the plasma image core** (kept above for history). The two third-party reference images are not committed | **Approved** |
| 22 Oct 2026 | **Desktop theme: dark.** A deep-space background replaces the bright Pearl desktop, because the Nova core needs darkness to glow. The light tokens in `design-tokens.json` become history for colour; geometry and motion tokens still apply. A dark token set is needed, and the accessibility rules below still bind (4.5:1 text contrast) | **Approved** |
| 21 Oct 2026 | **Desktop direction:** a small Nexus at bottom centre is the persistent launcher, with no conventional taskbar. Clicking it opens the full Nexus core and chat. Open applications appear as tiny planets orbiting it, which expand after a delayed hover into a wider orbit of larger, labelled, selectable targets | **Approved, not yet built** |
| 21 Oct 2026 | **Compositor: Hyprland.** It manages windows (tiling, snapping, workspaces); the SpaceUI shell runs on top of it | **Approved** |
| 22 Oct 2026 | **Shell technology: Quickshell** (QML, Qt 6) running on Hyprland, chosen after the [shell spike](../spaceui-spike.md); see [ADR-003](../../adr/003-desktop-shell.md) | **Approved** |
| 22 Oct 2026 | **Window manager: Desktop A, "Orbit dock"** (the current build: tiled windows, the small core launcher with open windows as planets, the top-right system orb), **plus Desktop C's workspace overview** as a quick switcher on Super+Tab: workspaces as cards around the core, with a search bar. Mocks: [canvas](https://claude.ai/artifact/R9EACfTrFZgo8924JqNVTQ) | **Approved** |
| 22 Oct 2026 | **Settings: B, "Orbital"**: categories as planets around the core, and the chosen one opens a glass panel beside it. It also takes the **search from C, which applies settings straight away** and opens a window listing every change it made, each with Undo. This is limited to reversible personal settings (display, sound, appearance, input); anything that changes the installed system still goes through Guardian (rule 3) | **Approved** |
| 22 Oct 2026 | **Coding agent: B, "Core timeline"**: the core and the plan on the left, steps as a timeline, and each proposed edit as a diff card with Accept, Reject and Suggest a change | **Approved** |
| 22 Oct 2026 | **System change approval (Guardian)**: the proposal dialog with a summary (packages, settings, risk), the configuration diff, the checks (policy, signed build, backup, fallback boot) and "Approve and stage" | **Approved** |
| 22 Oct 2026 | **Image generation: A, "Gallery and inspector"**, as the more practical layout. B's orbit of variations is liked; its refine chips (Warmer light, Wider view…) can carry over into A's inspector | **Approved** |
| (prototype) | The windowless core, app planets that emerge with intent, a standalone chat bar, the top-right orbital system panel, conventional Files and Settings windows, and magnetic window snapping with a layout chooser | **Approved baseline** (`screens/baseline-*`) |
| (prototype) | A launcher panel, a workspaces overview and a start menu | **Superseded** (`screens/early-*`), kept for history only |

## Rules that bind every implementation

These come from the brief, plus the NexusOS security model. They are where the
two meet:

1. **The AI only suggests.** App suggestions never launch apps, and system
   changes go through Guardian's preview → root approval → staged boot.
   One exception, approved on 22 Oct: settings search may apply reversible
   personal settings at once, provided it shows every change it made and
   each can be undone. Keep
   inputs, inferences, sources, suggested actions and completed actions
   visibly distinct.
2. **Never fake system status.** Sample data stays labelled as sample data
   until a real service confirms it. Quick controls read real hardware state
   when they start.
3. **Split settings by mechanism.** Runtime toggles (volume, Wi-Fi, Bluetooth
   power) go through normal system services. Persistent system configuration
   (for example enabling SSH or printing) goes through Guardian.
4. **The system overlay never sits above secure or lock surfaces.**
5. **Accessibility outranks pixel fidelity.** Essential text needs 4.5:1
   contrast and a 13–14 px body size. The prototype's 8–9 px helper text and
   pale greys (for example `#b0a5bd` on white, about 2.3:1) need fixing, not
   copying.

## Assets

| File | Use |
|---|---|
| `nexus-plasma-original.png` | The approved core: 1920×1920 RGBA, exactly as delivered |
| `nexus-pearl-original.webp` | The previous pearl core, kept for reference |

**Rendering rule: premultiplied alpha.** About half the plasma asset is
semi-transparent glow. If a shader samples or scales it with straight
(non-premultiplied) alpha, compositing over the desktop is off by up to 17
levels at launcher size: a 40× downscale to 48 px, measured against the
correct premultiplied result. Qt and browsers premultiply ordinary images
already; any custom core shader must premultiply before filtering or blending.

The file also has noisy RGB under fully transparent pixels: about 48% of
the image, with values up to near-full red, green or blue. Testing showed this
is **harmless**. The glow fades through low alpha before it reaches those
pixels, so the hidden colour never reaches the visible output at any scale
from 8× to 60×. No cleanup is needed.

## Not yet received

The brief references these, but they were not in the handover batches:

- `SpaceUI-design-specification.md`
- `acceptance-tests.md`
- `reference/prototype-source.zip`
- the prototype `assets/` folder

Until they arrive, the acceptance scenarios are taken from the brief, the
TODO file and `capture-screens.py`.

Three third-party reference images (the NexusAI1 and NexusAI4 inspirations)
were shared during design but are deliberately not committed: they may be
copyrighted stock imagery, and one carries a watermark.
