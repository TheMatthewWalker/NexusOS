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
`POST /guardian/review`, `POST|GET|DELETE /journal`,
`GET /journal/today/summary`, `POST /journal/{id}/undo`, `POST /fixit`, `POST /fixit/apply`,
`GET /health/report`.

## Activity journal

One per-user, append-only log of what happened, in the `journal` table of
Core's `state.sqlite3` (added by the `schema_journal_v1` migration, which
commits with its marker in one transaction). Each entry has a UTC time, a
kind (`setting`, `app`, `system`, `ai-read`, `egress`, `device`), an actor
(`user`, `nexus` or an app name), a one-line summary, and optional
`before`/`after` JSON, `reversible` flag and `ref`.

- **Core records what it observes.** Every cloud request is journalled as
  `egress` just before it is sent, with the provider, host and consent time
  (when the person selected the cloud provider; the operator's
  `NEXUS_CLOUD_EGRESS_ALLOWED` is the precondition). It never holds prompt or
  answer text. Guardian proposals, submissions and the broker's decisions are
  `system` entries. Core reads no files or context to answer today, so it
  writes no `ai-read` entries yet.
- **The shell records the rest.** `POST /journal` accepts only `setting` and
  `device` entries, never as actor `nexus`; summaries are at most 200
  characters, `before`/`after` at most 4 KB each, 60 writes a minute.
- **Undo** (`POST /journal/{id}/undo`) applies to reversible `setting` entries
  with a `before` value. Core cannot change shell settings, so it returns the
  value to restore, marks the entry undone, and refuses a second undo.
- **Retention and erasure follow history.** Entries older than
  `NEXUS_HISTORY_MAX_DAYS` are hidden and pruned by both history and journal
  writes, with a hard cap of 10,000 rows. `DELETE /journal?day=YYYY-MM-DD`
  ("forget today") deletes that local day's entries with `secure_delete`, and
  checks none remain before committing. `GET /journal/today/summary` gives the
  privacy dashboard's counts: left the computer, AI reads, device uses.
- **CLI:** `nexus journal [--today] [--kind K]`, `nexus journal forget-today`.

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
