# NexusOS — roadmap

**Baseline:** 2026-10-14. **Updated:** 2026-10-21. Milestones are evidence-gated, not dated.

A milestone is **DONE** only when every gate has reproducible evidence: CI,
VM tests and, where stated, real hardware. Passing CI or a VM is never
treated as hardware acceptance. Evidence lives in [docs/evidence](evidence/).
Older dated progress notes are kept in [history](evidence/history.md).

## Status

| Gate | Milestone | Status | Evidence |
|---|---|---|---|
| M0 | Repository and bootstrap | **DONE** | Core CI on Ubuntu |
| M1 | Nexus Core v0.1 | **DONE except 1 operator gate** | Real Ollama and llama.cpp engines pass 17 live checks; CI pulls a real model. Live cloud answer needs your API key ([M1](evidence/M1.md)) |
| M2 | Hardened Core v0.2 | **DONE (pending review)** | Gates #50–#53 automated in CI, including two real Linux accounts, a real keyring, DNS/egress and log capture ([M2](evidence/M2.md)) |
| M3 | Guardian preview and approval | **DONE in VM (pending review)** | Shared schema, Ed25519 attestations, root broker as a hardened NixOS service, root-console approval, AI-proposed typed plans with injection tests ([M3](evidence/M3.md), [ADR-002](adr/002-guardian-candidate-model.md)) |
| M4 | Guardian rollback | **VM DONE; hardware open** | One-time trial boots on a real GRUB disk. A healthy trial is confirmed; a broken trial panics and falls back unattended. User-data snapshots are separate from OS generations ([M4](evidence/M4.md)). Physical-hardware drill outstanding |
| M5 | Inference manager | **IN PROGRESS: tooling done, hardware open** | Provider registry, managed llama.cpp (start on demand, stop on unload), token budgets, RAM/VRAM telemetry, benchmark through Core, CI CPU run ([M5](evidence/M5.md)). 8 GB and 24 GB GPU runs and the gaming-reclaim test outstanding |
| M6 | Desktop alpha | **DESIGN APPROVED; SHELL CHOICE OPEN** | SpaceUI design pack archived ([docs/design/spaceui](design/spaceui/README.md)). The shell spike recommends native Quickshell ([report](design/spaceui-spike.md), [ADR-003](adr/003-desktop-shell.md)); it waits on your choice |
| M7 | OS alpha image | TODO | Flake packages and NixOS modules for Core and Guardian exist (`flake.nix`); ISO, onboarding and hardware matrix not started |
| M8 | Context and search | TODO | |
| M9 | Automation and skills | TODO | |
| M10 | Voice and vision | TODO | |
| M11 | Advanced optional | TODO | |

## Gates that need you or real hardware

These cannot be completed from CI or a VM. Each has a written procedure.

1. **M1: live cloud answer.** One run with your own API key ([procedure](evidence/M1.md#gate-still-requiring-the-operator-live-cloud-answer)).
2. **M2/M3: independent security review.** Read [ADR-002](adr/002-guardian-candidate-model.md) and the evidence files, then record sign-off in each.
3. **M4: physical recovery drill.** On a spare NixOS machine, stage a deliberately broken candidate and confirm unattended fallback ([procedure](evidence/M4.md#physical-hardware-drill)).
4. **M5: GPU profiles.** Run `scripts/bench/nexus_inference_bench.py` on an 8 GB and a 24 GB GPU machine, and do the game-launch VRAM reclaim check ([procedure](evidence/M5.md)).

## M1 — working Nexus Core

- [x] Core and CLI compile cleanly on Ubuntu CI and in the Nix package.
- [x] CLI `health` and `system` succeed; `providers` and `use` are deterministic.
- [x] A real Ollama model returns a non-empty answer (real engine in the dev container; real `qwen2.5:0.5b` in CI).
- [ ] With a valid API key, the cloud adapter returns a non-empty answer. **Operator gate.** Missing-key and no-consent paths fail clearly (tested).
- [x] Malformed prompts are rejected; the LLM cannot execute shell commands or change the system.
- [x] Integration tests use fake model endpoints in Core CI, plus real engines in M1 Live Model.
- [x] The Unix socket is the default, with no TCP listener; secrets and prompts never appear in logs.

## M2 — safe foundation

- [x] IPC authenticates the invoking local user (`SO_PEERCRED` plus bearer token; mutation-verified).
- [x] Provider credentials come from the OS keyring only (real gnome-keyring CI).
- [x] Per-user SQLite with retention, atomic migration and verified erasure.
- [x] Rate limiting, concurrency backpressure and cancellation.
- [x] Explicit cloud egress consent; no implicit failover.
- [x] Request and response bounds, endpoint allowlisting, DNS rebinding and SSRF protections tested.
- [ ] Independent security review sign-off.

## M3 / M4 — never brick the machine

- [x] The AI generates only typed plans, validated against one shared schema.
- [x] Guardian enforces policy in a separate root service, independently of the model.
- [x] Every change shows its module, risk, root approval and rollback target.
- [x] Signed build attestations bind evaluate, build and VM-test results to the exact system output.
- [x] Boot-failure rollback exercised after an intentionally broken deployment, unattended (VM).
- [x] Home-directory snapshots handled separately from OS generations.
- [ ] Physical-hardware recovery drill. **Hardware gate.**
- [ ] Desktop-user approval delegation (for example polkit), so approval doesn't need a root console. Design input needed; see ADR-002.

## M5 — M7 — AI-native distribution

- [x] Managed llama.cpp lifecycle (start on demand, health-gated, stopped on unload or Core shutdown).
- [x] Benchmark harness measuring cold start, latency and memory reclaim through Core; CPU profile in CI.
- [ ] 8 GB and 24 GB GPU benchmark profiles. **Hardware gate.**
- [ ] A game launch reclaims VRAM without killing the desktop. **Hardware gate**, and it needs foreground-app detection (M6).
- [ ] The GUI allows normal non-AI system operation when inference is offline. **M6, needs your design input.**
- [ ] The installer produces repeatable images with a documented recovery path (M7).

## M8 — M11 optional scope

Nexus Search, Memory, Context Engine, Automations, Skills, Voice, Vision,
Workspaces, Remote, Explain, a self-documenting OS, a troubleshooting agent,
system migration, multi-machine inference, marketplace and AI development
mode. All privacy-sensitive features are opt-in, isolated per user and
independently permissioned.

## Definition of done (each milestone)

Documented API/CLI; automated unit and integration checks; security review;
log and telemetry behaviour; install, remove and rollback instructions; a
reproducible acceptance demonstration. Source files existing is never
enough to call a milestone shipped.

## Next steps

1. Merge the open stacked PRs after review.
2. Complete the four operator and hardware gates above.
3. **M6 desktop:** agree the interaction design (command palette, provider switch, memory widget, change-proposal view, offline fallback) before any UI work.
4. **M7:** a NixOS installer image built from `flake.nix`, onboarding (key provisioning, allowed users) and a supported-hardware matrix.
