# Nexus AI · core approval study

**16 October 2026 · pending user approval**

The separate `/core-study` route creates and animates a new original plasma/nebula core. It is intentionally outside `DesktopWorkspace`; the existing `/` desktop retains its prior core, taskbars, windows and orbital controls. The new asset is not used in the desktop pending confirmation.

## Visual direction

The supplied NexusAI1 reference informs the blue/cyan/violet/magenta palette and warm amber heart. NexusAI4 informs inclined orbit-like plasma strands, not its watermarked planet composition. The original soft Nexus sphere supplies the approachable material direction. The generated 1920 × 1920 PNG has transparency and is stored locally and in project storage.

## Motion

The renderer advects several noise fields to distort the gas texture locally, adds a gentle angular/radial deformation, and overlays moving inclined plasma strands with front/back attenuation. It does not rotate the entire image. Idle flow is slow; hover/focus awakens it; simulated response uses a smoothed multiband envelope to drive gas displacement, luminosity and flow speed. The response envelope is illustrative, not actual speech analysis, AI streaming or microphone capture.

The study includes a large core and a small launcher-scale core, an energy adjustment, additional strand toggle, pause/resume, and light/dark reference backplates. The strand toggle affects the procedural flowing strands; the asset's painted orbit remains part of its design. Both scales support hover/focus and click to toggle simulated response.

OS reduced-motion preference applies immediately and freezes motion. WebGL/image errors or context loss use a still-image fallback with explicit status. Image loading has a portable local fallback, and GPU resources/listeners are disposed when leaving the study. The bright backplate is the main OS-fit reference; the dark backplate is only an inspection aid, not a proposed dark OS theme.

## Deferred desktop direction

After core approval, the user wants only a small bottom-centred Nexus in the idle desktop; no conventional taskbar and no always-visible full AI/chat. Clicking the small Nexus opens the full AI/chat. Open applications appear as tiny planets orbiting it; delayed hover expands them to a wider radius and larger labelled selectable targets. These changes have been recorded but not integrated or represented as an approved implementation yet.

## Verification

TypeScript and the existing 19 unit tests pass. `scripts/verify-core-study.py` verifies two WebGL renderers, actual frame changes, hover/response state, pause, live reduced-motion changes, compact width, context-loss fallback, and that the existing desktop controls remain present. Production build passes. This is a core design approval prototype, not hardware/audio/AI integration.
