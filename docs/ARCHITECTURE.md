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
| `Nexus.Desktop` | Not started (M6) | Hyprland/Quickshell command palette, provider widget, change-proposal view |
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
8. **Containers cannot prove a kernel, bootloader or GPU driver works.**
   Use VMs, then staged real boots on hardware.

## Core API (all endpoints require authentication)

`GET /health`, `GET /system`, `GET /providers`, `POST /providers/select`,
`POST /chat`, `GET|DELETE /history`, `GET /inference/status`,
`POST /inference/unload`, `GET /inference/telemetry`,
`POST /guardian/preview`, `POST /guardian/propose`,
`POST /guardian/submit`, `GET /guardian/candidates/{id}`,
`POST /guardian/review`, `POST /fixit`, `POST /fixit/apply`,
`GET /health/report`.

## Probes and Fix it

Design: [features.md](design/features.md) ("Probes", features 2 and 14).

- **Probes** (`Probes.cs`) are a fixed, in-code allow-list of read-only
  diagnostics: an id, an area (sound, network, display, power, disk, app),
  an exact argv started without a shell, a timeout and an output cap.
  Binaries resolve only from fixed system directories (`NEXUS_PROBE_PATH`
  overrides them); a missing tool reports `unavailable`, never a guess.
  Output is untrusted text, parsed into bounded records.
- **Fix library** (`src/NexusOS.Core/fixes/*.json`, embedded and validated
  at startup) holds known problems as data: an area, match clauses (a regex,
  structured field conditions, or a probe status), a plain-English cause and
  one or two fixes. A `personal` fix names an action from the in-code
  allow-list (`PersonalActions`), whose parameters are bound from probe
  records and must match a strict pattern. A `system` fix is only a
  description for a Guardian proposal.
- **`POST /fixit`** classifies the area by keywords, or (only when no
  keyword matches, and only with a local provider) asks the model for
  `{"area": ...}` under a strict schema; anything else falls back to checking
  every area. The model never sees probe output and never chooses a fix or
  command. Core runs the area's probes, matches the library and returns the
  cause, short sanitised evidence excerpts, the fixes and a one-use evidence
  token.
- **`POST /fixit/apply`** runs a personal fix only if it belongs to the latest
  diagnosis and the token matches and is under 10 minutes old (410 when
  stale, 404 for a fix not in that diagnosis). System fixes return 409
  "needs approval" and are never applied by Core.
- **`GET /health/report`** runs the health probes and fills fixed templates
  with validated figures, plus any library problems found. No model.
