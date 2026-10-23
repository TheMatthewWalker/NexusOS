# NexusOS Architecture Decision Record 001

## Goals

AI is the primary *optional* interaction channel. Linux stays usable when
inference or the internet is unavailable. Model output cannot authorise its
own privileged actions.

## Components

| Component | Status | Role |
|---|---|---|
| `Nexus.Core` (`src/NexusOS.Core`) | Implemented | Unprivileged per-user daemon: provider registry (`IModelProvider`), history, token budgets, telemetry, Guardian plan proposal and broker client |
| `nexus` CLI (`src/NexusOS.Cli`) | Implemented | User interface to Core over its socket |
| `Nexus.Inference` | Partly implemented (in Core) | Ollama and llama.cpp adapters, managed `llama-server` lifecycle, unload and idle eviction, RAM/VRAM telemetry, benchmark harness. Foreground preemption is open |
| `Nexus.Guardian` (`scripts/guardian`, `nix/modules/guardian.nix`) | Implemented | Root broker, root approval console, Ed25519 attestation tool, staging executor, boot check |
| `Nexus.Desktop` (`shell/`, `nix/modules/desktop.nix`) | In progress (M6) | The Hyprland session and the Nexus shell (Quickshell, ADR-003), comprising the wallpaper, launcher, Nova core, app orbit, chat and system panel. It reaches Core only through `nexus bridge` |
| `Nexus.Search`, `Nexus.Skills` | Not started (M8/M9) | Permission-aware indexing; capability-scoped, signed tools |

## Trust boundaries

1. **LLM text is untrusted data.** It can only become a typed plan preview,
   validated against one shared schema, and never directly a command,
   approval or submission.
2. **Core is unprivileged and per user.** Its Unix socket is in a 0700
   directory. Callers are identified by kernel `SO_PEERCRED` and must also
   hold a bearer token. TCP is a loopback-only development opt-in.
3. **Guardian decides as root, in a sandbox.** It has no capabilities and
   no network. It re-observes the running generation itself, accepts only
   root-console approvals and verifies Ed25519 builder attestations with
   public keys only (ADR-002).
4. **Only the staging executor and boot check touch the system profile and
   bootloader**, and only via `switch-to-configuration boot` plus a GRUB
   one-time entry. The running system is never switched.
5. **Recovery needs no LLM, network or user input.** A failed trial boot
   resets (`panic=`) and GRUB returns to the saved baseline.
6. **Cloud egress requires explicit operator consent.** The provider key
   lives only in the OS keyring, and every resolved address is validated
   before connecting.
7. **System rollback never restores mutable home data.** User data has its
   own snapshot tool.
8. **The desktop shell holds no secrets and gets no privileges.** It runs as
   the user, reaches Core only through the `nexus bridge` CLI, renders model
   output as plain text, and sits below lock screens. App suggestions name
   catalogue roles only; opening an app is always the user's action.
9. **Containers cannot prove a kernel, bootloader or GPU driver works.**
   Use VMs, then staged real boots on hardware.

## Core API (all endpoints require authentication)

`GET /health`, `GET /system`, `GET /providers`, `POST /providers/select`,
`POST /chat`, `POST /chat/stream` (NDJSON pieces, then `done` or `error`),
`POST /suggest` (catalogue rules), `POST /suggest/model` (the model picks from
the catalogue and falls back to the rules, labelled as such), `GET /apps`,
`GET|DELETE /history`, `GET /inference/status`,
`POST /inference/unload`, `GET /inference/telemetry`,
`POST /guardian/preview`, `POST /guardian/propose`,
`POST /guardian/submit`, `GET /guardian/candidates/{id}`,
`POST /guardian/review`.
