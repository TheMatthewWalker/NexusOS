# NexusOS — roadmap

**Baseline:** 2026-10-14. **Updated:** 2026-11-19. Milestones are evidence-gated, not dated.

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
| M6 | Desktop alpha | **BUILT; hardware open** | Hyprland with the Nexus shell (SpaceUI): Nova core, app orbit, streamed answers, floating or tiled windows with a top bar, Settings (every page, with search and AI help), graphical login and lock screens, Apps (Flathub, apt in a container, Try it), Code, Images, Explain, Describe this, voice, workspaces, game mode. VM tests cover the desktop and login ([M6](evidence/M6.md)). Not yet run on your laptop end to end |
| M7 | OS alpha image | **BUILT; hardware open** | `nix build .#iso`: a live image whose graphical installer finds the hardware, picks drivers and a model, chooses keyboard and time zone, encrypts the disk (LUKS2) and installs offline, also beside Windows. The `nexus-installer` VM test installs to a blank encrypted disk and boots it in CI. A real-hardware install and the supported-hardware list are outstanding |
| M8 | Context and search | **BUILT** | Search by words or meaning (Super+S), Nexus reads what it is allowed to (`inspect_system`, files, journal), teaching notes, the activity journal and Timeline |
| M9 | Automation and skills | **BUILT** | Automations in plain words (rules engine), Fix it with read-only probes, Welcome bundles, Move in from an old Windows, Mac or Linux drive |
| M10 | Voice and vision | **BUILT; hardware open** | Hold-to-talk voice, spoken replies, Describe this with a local vision model. Not yet tried on real microphones and screens |
| M11 | Advanced optional | **PARTLY BUILT** | The large model (gpt-oss-20b, mixture of experts on llama.cpp) and the very large model (colibri, experts read from the disk, #68). Multi-machine inference and a marketplace are not started |

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
- [x] Desktop-user approval through polkit (password every time), with a local builder and systemd-boot trial boots ([ADR-002](adr/002-guardian-candidate-model.md#desktop-approval)). Needs the VM runs and a real-hardware approval drill.

## M5 — M7 — AI-native distribution

- [x] Managed llama.cpp lifecycle (start on demand, health-gated, stopped on unload or Core shutdown).
- [x] Benchmark harness measuring cold start, latency and memory reclaim through Core; CPU profile in CI.
- [ ] 8 GB and 24 GB GPU benchmark profiles. **Hardware gate.**
- [ ] A game launch reclaims VRAM without killing the desktop. **Hardware gate.** Game mode is built: the shell detects a fullscreen game, unloads the model through the bridge, reports the VRAM freed, and turns off Hyprland blur and animations until the game closes ([shell/README.md](../shell/README.md#game-mode)). Still needs the check on a real GPU machine ([procedure](evidence/M5.md)).
- [x] The GUI allows normal non-AI system operation when inference is offline. With Core stopped, the shell says so and apps still open (`hyprland-desktop` VM test, [M6](evidence/M6.md)).
- [x] The installer produces repeatable images (`nix build .#iso`) and installs them, checked end to end by the `nexus-installer` VM test ([M7](evidence/M7.md)). Recovery is systemd-boot's earlier system versions ([INSTALL](INSTALL.md)); a real-hardware install is still an operator gate.

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

## What is left before a 1.0

**Needs your decision**

1. **Publish the first release.** NexusOS updates are built: installed
   computers follow the `release` branch and Guardian offers each new release.
   Make the repository public, then push a tag (`git tag v0.4.0 && git push
   origin v0.4.0`): `.github/workflows/release.yml` builds the image, publishes
   it in parts under GitHub's 2 GB limit, and moves `release`. For one-file
   downloads, also host the image somewhere without that limit (SourceForge is
   free for open-source projects; Cloudflare R2 is cheap with free downloads).

**Needs real hardware (procedures in the evidence files)**

4. Install from the USB stick on your laptop, offline, with encryption; send
   `/var/log/nexus-install.log` if anything fails.
5. Guardian on the installed system: a change approved with your password, its
   trial boot, and a deliberately broken one falling back (M4 drill).
6. The large and very large models on 16 GB: download, speed, tool calls.
7. GPU profiles (8 GB and 24 GB) and the game-mode VRAM check (M5).
8. Voice, Describe this, Move in from a real Windows drive.

**Still to build**

9. The start-up screen (Plymouth, built): check it on real hardware with the
   install in 4, including typing the disk's password on it.
10. Secure Boot (signed boot files, for computers that will not start without it).
11. More languages than English, for the desktop and Nexus.

**Review**

12. An independent security review of Core, Guardian and the installer
    (M2/M3 sign-off), and one live cloud answer with your API key (M1).
