# NexusOS — executable project roadmap

**Baseline:** 2026-10-14. **Cadence:** milestone-based, not promised delivery dates. **Status terminology:** TODO / IN PROGRESS / DONE / BLOCKED. A milestone becomes DONE only after its acceptance criteria have evidence in CI, tests, and (where required) real hardware.

## Release gates

| Gate | Milestone | Acceptance criteria | Status |
|---|---|---|---|
| M0 | Repository/bootstrap | Build on Ubuntu in GitHub CI; test project runs; README reproduces local setup | TODO (files drafted) |
| M1 | Nexus Core v0.1 | Health, provider listing/selection, read-only system inventory, local Ollama chat, cloud OpenAI-compatible chat; CLI returns meaningful errors | TODO (implementation drafted, not compiled) |
| M2 | Hardened Core v0.2 | Unix socket or authenticated loopback, per-user storage, encrypted secrets, persistent provider selection, log redaction, request limits, mock-provider integration tests | TODO |
| M3 | Guardian preview | Typed change schema; immutable audit log; Nix diff; explicit confirmation; no arbitrary shell | TODO |
| M4 | Guardian rollback | Reproducible Nix builds, VM smoke checks, staged boot, known-good recovery demonstrated on test hardware | TODO |
| M5 | Inference manager | llama.cpp adapter, context/KV budget, VRAM/RAM telemetry, model eviction and idle sleep; application foreground priority | TODO |
| M6 | Desktop alpha | Hyprland/Quickshell command palette, provider switch, memory widget, visual change proposal and offline fallback | TODO |
| M7 | OS alpha image | Reproducible bootable NixOS ISO, onboarding, supported hardware matrix, rollback and recovery documentation | TODO |
| M8 | Context/search | Permission-aware semantic indexing, opt-in workspace context, retention/delete/export controls | TODO |
| M9 | Automation/skills | Event scheduler, signed scoped tools, per-action approval and sandbox abuse tests | TODO |
| M10 | Voice/vision | Offline optional STT/TTS, screen selection consent, protected-window handling, device battery profiles | TODO |
| M11 | Advanced optional | MoE paging experiments with measured tokens/s, distributed inference, remote device control, workspace migration and marketplace | TODO |

## MVP (M1) objective — working Nexus Core

- [ ] .NET Core server compiles cleanly on Ubuntu and supported Linux workstation.
- [ ] CLI calls `/health` and `/system` successfully.
- [ ] `providers`, `use local`, `use openai-compatible` behave deterministically.
- [ ] With Ollama running and configured model pulled, `ask` returns a non-empty model answer.
- [ ] With a valid API key, cloud adapter returns a non-empty answer; missing token fails clearly.
- [ ] Malformed prompts are rejected; LLM cannot execute shell commands or mutate system.
- [ ] Integration tests use a fake model endpoint to run deterministically in CI.
- [ ] Security checklist verifies service is loopback only; secrets are never logged.

## M2 objective — safe foundation
- [ ] IPC authenticates the invoking local user.
- [ ] Provider credentials stored outside config files in an OS keyring.
- [ ] Chats and routing preferences persist to per-user SQLite, with retention settings.
- [ ] Rate limiting and cancellation handle long requests.
- [ ] Cloud provider failover is opt-in, with explicit data egress policy.
- [ ] API request size, endpoint allowlisting and SSRF protections are tested.

## M3–M4 objective — never brick the machine
- [ ] AI generates only typed plans, not privileged shell strings.
- [ ] Guardian enforces a permission policy independently of the model.
- [ ] Every change displays diff, risk, approval and expected rollback path.
- [ ] Nix evaluation/build and applicable disposable VM tests pass.
- [ ] Boot-failure rollback exercised after an intentionally invalid deployment.
- [ ] Home-directory snapshots/backups handled separately from OS generations.

## M5–M7 objective — AI-native distribution
- [ ] Measure model cold start, idle VRAM, active VRAM, token latency on CPU-only, 8GB GPU and 24GB GPU reference profiles.
- [ ] Gaming workload reclaims VRAM without killing the desktop.
- [ ] GUI allows normal non-AI system operation when inference offline.
- [ ] Installer produces repeatable images and has a documented recovery path.

## M8–M11 optional feature scope
Nexus Search; Nexus Memory; Nexus Context Engine; Nexus Automations; Nexus Skills; Nexus Voice; Nexus Vision; Nexus Workspaces; Nexus Remote; Nexus Explain; self-documenting OS; troubleshooting agent; system migration; multi-machine inference; marketplace; AI development mode. All privacy-sensitive features opt-in, per-user isolated, and independently permissioned.

## Definition of done (each milestone)
Documented API/CLI; automated unit and integration checks; security review; log/telemetry behaviour; install/remove/rollback instructions; reproducible acceptance demonstration. No TODO milestone is called shipped merely because source files exist.

## Next actions
1. Run CI for initial source tree; repair any build/test errors (M0).
2. Add fake Ollama and OpenAI HTTP integration tests, then run against a real local model (M1).
3. Harden IPC, credential management and persistent state before enabling external plugins (M2).
