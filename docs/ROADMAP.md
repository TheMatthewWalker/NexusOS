# NexusOS — executable project roadmap

**Baseline:** 2026-10-14. **Cadence:** milestone-based, not promised delivery dates. **Status terminology:** TODO / IN PROGRESS / DONE / BLOCKED. A milestone becomes DONE only after its acceptance criteria have evidence in CI, tests, and (where required) real hardware.

## Release gates

| Gate | Milestone | Acceptance criteria | Status |
|---|---|---|---|
| M0 | Repository/bootstrap | Build on Ubuntu in GitHub CI; test project runs; README reproduces local setup | DONE — CI verified |
| M1 | Nexus Core v0.1 | Health, provider listing/selection, read-only system inventory, local Ollama chat, cloud OpenAI-compatible chat; CLI returns meaningful errors | PARTIAL — mock CI passed; physical Ollama/cloud test open |
| M2 | Hardened Core v0.2 | Authenticated IPC, per-user storage, OS-keyring credentials, retention, request limits and provider isolation | IN PROGRESS — bearer auth, history limits, rate limits, endpoint restrictions; credential/keyring work open |
| M3 | Guardian preview | Typed plans, policy, Nix diff, approval, privileged immutable audit, no shell | IN PROGRESS — safe preview and review-only hash chain; execution boundary and Nix diff open |
| M4 | Guardian rollback | Reproducible Nix builds, VM smoke checks, staged boot, known-good recovery demonstrated on test hardware | TODO |
| M5 | Inference manager | llama.cpp adapter, KV budget, VRAM/RAM monitoring, eviction/idle sleep and application priority | IN PROGRESS — Ollama status/unload and budget maths; runtime policy and measurements open |
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


## Evidence and outstanding gates (2026-10-14)

- **M0:** GitHub CI builds and tests source on Ubuntu.
- **M1:** Mock Ollama end-to-end chat path works; an actual downloaded model and paid cloud API remain **unverified**.
- **M2:** Per-user bearer authentication, persistent settings/history, deletion, basic rate limiting and provider URL restrictions exist. Remaining: OS-keyring secrets, Unix socket peer credentials, bounded provider responses, multi-user boundaries, comprehensive logging/egress protections.
- **M3:** Typed allowlisted Nix preview, explicit non-executing acknowledgement and hash-linked user-file audit are implemented. Remaining: trusted privileged broker, protected audit store, real Nix diff and policy enforcement.
- **M4:** **NOT STARTED.** Requires NixOS builds, disposable VM boot testing, staged generation promotion and tested physical recovery, including user-data backups. The current Guardian cannot apply changes.
- **M5:** Backend availability and explicit Ollama unload request plus budget arithmetic exist. Remaining: llama.cpp lifecycle, VRAM/RAM telemetry, KV budgeting, idle eviction, foreground preemption and benchmarks on real GPUs.

No milestone M2–M5 is DONE until its own definition-of-done criteria are satisfied. No desktop/UI work begins before these are addressed.
