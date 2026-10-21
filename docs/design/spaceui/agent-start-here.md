# SpaceUI — next-agent brief

## Reading order

Read `SpaceUI-design-specification.md` first. Use its status vocabulary to separate approved design, current demo behavior, production requirements and open decisions. Then inspect `design-tokens.json`, the four current screenshots in `assets/`, and `acceptance-tests.md`. The exact working prototype is supplied in `reference/prototype-source.zip`, commit `bc1c32f608cbda66703fcaf706835c38a8388dbc`.

The source screenshot/asset pack is self-contained. Do not depend on the temporary Manus preview or the managed storage URL for the sphere. The local transparent asset is `assets/nexus-pearl.png`, also present in the source at `client/public/assets/nexus-pearl.png`. Preserve it or reproduce its soft identity accurately.

## Ready-to-paste assignment

> You are continuing **NexusOS**, a Linux-based OS for AI-focused workflows. Its interface is **SpaceUI**. Implement/build from the attached SpaceUI design pack; do not invent a new visual direction. Read the full specification, tokens and acceptance scenarios before changing code.
>
> The approved design is bright, modern and calm, using subtle nebula/space inspiration rather than a dark sci-fi theme. The assistant is **windowless**: a soft, translucent pearl-nebula sphere, a standalone chat/search bar below it, stable relevant app planets around it, and short readable replies/actions/context below the input. The user explicitly preferred this softer sphere to the old textured shader. Preserve the responsive app-suggestion motion that already works.
>
> Apps emerge from the core on gently curved paths as typed intent or responses make them relevant; obsolete apps return to the core. Each app keeps a fixed slot. Travelling targets are not interactive. Settled icons do not continuously orbit. Suggestions do not automatically launch applications. Preserve reduced-motion alternatives and meaningful textual status.
>
> A separate **top-right orbital system panel** is only a Nexus icon when collapsed. Hover/focus expands it above ordinary windows; click/tap also supports access. Every setting planet has a descriptive hover/focus tooltip and opens the corresponding category in the complete Settings application. Quick volume/Wi-Fi/Bluetooth controls are separate direct actions and share state with full Settings. Do not hide the system overlay behind maximised windows or allow it above secure/lock surfaces.
>
> **Files** uses conventional Places navigation, breadcrumbs, current-folder search, list/grid, selection and preview. **Settings** uses a category sidebar and readable grouped controls. Both use pearl surfaces and slate content text. Other Linux applications retain their ordinary content UI; SpaceUI provides compatible windowing behavior rather than a radial redesign of every app.
>
> The window layer supports focus, move, edge/corner resize, minimise/restore, maximise/restore, close, magnetic edge/corner snapping, a layout chooser, a live snap preview and Escape cancellation. Two snapped columns have one shared divider that resizes both. Content adapts to its **window width**, not only screen width. Minimise preserves application state; retiling preserves the original floating restore bounds. Handle native app minimum sizes, focus, documents and unsaved-work semantics when doing a real OS integration.
>
> First reproduce and verify the approved interaction baseline. The supplied React project is a **browser prototype**, not a compositor, live AI backend, filesystem or hardware controller. Its local example replies, sample files, preview terminal and settings must remain truthfully labelled until real services acknowledge operations. Do not copy simulated battery/clock/network values into production as real status or overwrite hardware settings with sample defaults.
>
> Keep inputs, inferences, sources, suggested actions and completed actions distinct. Context must be explicitly scoped and inspectable. No silent reading of the desktop/filesystem/browser history, no invented citations, and no broad context upload by default. The assistant does not need permission to navigate its sample UI; actual protected/consequential operations require the appropriate explicit authority.
>
> Follow the spec's component/service boundaries. Native compositor/toolkit/decoration strategy, AI provider/data egress, persistent history, full Files operation scope, multi-monitor policy and keyboard windowing bindings are **open decisions**. Do not silently choose an architecture that changes the product. Isolate those dependencies behind adapters and surface material decisions before real integration. Low-risk spacing/implementation cleanup should follow the rationale without repeatedly asking about every pixel.
>
> Correct known prototype caveats without redesigning it: Escape priority across layers, keyboard focus restoration, compact geometry coherence, per-client tile minimums, Files header/row alignment, consistent adaptive preferences and accessibility target/contrast checks. The soft-core and desktop-wide motion rules live in `desktop.css` as well as `index.css`; do not accidentally resurrect legacy shader CSS.
>
> Deliver runnable source, a concise implementation/change log distinguishing mocks from real integrations, screenshots of the core/orbit, Files + orbital panel, full Settings and paired tiles, and test evidence mapped to the acceptance IDs. Flag any material deviation from this design. Do not declare production completion from screenshots alone.

## Prototype startup

Extract the source archive into its own directory. Use Node 22 and pnpm 10.18.0 as the reference toolchain:

```bash
pnpm install --frozen-lockfile
pnpm dev
```

The reference runtime listens on port 3000. The frontend's sphere asset includes a local fallback. Manus-specific server configuration is an integration concern, not a requirement to obtain the originating task's credentials. If running outside Manus, keep preview-only behavior and stub/disable unrelated platform account features rather than requesting private environment values.

```bash
pnpm check
pnpm test
pnpm build
python3 scripts/verify-desktop.py
python3 scripts/verify-motion.py
python3 scripts/verify-accessibility.py
```

Browser scripts require Python Playwright and Chromium, with the reference executable `/usr/bin/chromium`. Update the executable path for the target environment; do not report a missing browser package as a design failure.

## Expected first response from the implementing agent

Briefly identify the baseline and the intended build target, list material unresolved decisions if they block native integration, and proceed with the faithful baseline/adapter work that does not depend on them. Do not start with a fresh moodboard or another orbit concept.
