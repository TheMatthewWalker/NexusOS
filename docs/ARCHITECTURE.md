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

## Rules engine (automations in plain words)

Feature 9. A rule is data in a fixed schema, kept in the `rules` table of
Core's `state.sqlite3` (added by the `schema_rules_v1` migration, which
commits with its marker in one transaction):
`{id, name, enabled, trigger, conditions[], actions[], createdFrom, history}`.

- **Fixed lists.** Triggers: `device.connected` / `device.disconnected`
  (kind `audio`, `display` or `usb`, optional part of the device name),
  `time.daily` (`HH:MM`, optional days), `app.opened` (app id),
  `network.joined` (SSID), `power.plugged` / `power.unplugged`. Conditions:
  `time.between`, `time.days`, `power.is`. Actions: `app.open` (app id),
  `setting.set` for the reversible personal settings `volume`,
  `notifications.volume`, `night-light`, `do-not-disturb` and `wallpaper`
  (a name from the wallpaper library, never a path), `notify` (text),
  `backup.run` (profile). At most 4 conditions and 5 actions. Anything else
  is rejected; one validator reads model output, API input and stored rows.
- **No system changes.** The schema has no command, package, service or file
  action. A sentence or model output asking for one (or naming an
  interpreter or privileged tool as the app) is refused with "System changes
  go through approval"; those stay Guardian proposals. A rule that opens the
  app that triggers it is rejected.
- **Parsing.** `POST /rules/parse {sentence}` returns a draft and saves
  nothing. System-change sentences are refused before the model is asked.
  Otherwise the selected model (same cloud consent as chat) gets the lists
  and the fenced sentence and must reply with one strict JSON rule or a
  refusal code (`system-change`, `unsupported`); free text is never accepted.
  If the model is unavailable or its output is invalid, a small set of
  keyword patterns is tried (`source: keywords-fallback`); if they do not
  understand every part, the answer is "Couldn't turn that into a rule" with
  suggestions. The sentence shown on the card is rendered by Core from the
  typed rule, never taken from model text.
- **Saving and managing.** `POST /rules` saves a confirmed rule (re-validated;
  at most 100). `GET /rules`, `POST /rules/{id}/pause|resume`,
  `DELETE /rules/{id}` (removes its history too), `GET /rules/{id}/history`.
  Like every Core endpoint, all of these need the bearer token.
- **Running.** The shell reports what it observes with `POST /events`
  (device, app, network and power events; same field rules as triggers;
  120 a minute). Core matches enabled rules, checks conditions (an unknown
  power state never counts as met), records a run and returns the actions in
  the response for the shell to perform: Core cannot open apps or change
  shell settings. A 20-second timer fires `time.daily` rules in local time
  (once a day, within 2 minutes of their time) and queues their actions for
  `GET /rules/pending-actions?after=<cursor>`; queued actions expire after
  5 minutes rather than run late. Each rule fires at most once a minute, so
  flapping devices or rules that open each other's apps cannot loop.
- **Run history follows chat history.** Runs older than
  `NEXUS_HISTORY_MAX_DAYS` are hidden and pruned; `NEXUS_HISTORY_LIMIT` caps
  runs per rule (0 records none), with a hard cap of 10,000; `DELETE
  /history` erases every run (rules stay). Rule contents are never logged.
- **CLI:** `nexus rules`, `nexus rules add "<sentence>" [--yes]` (shows the
  draft and asks before saving), `nexus rules pause|resume|rm <id>`,
  `nexus rules history <id>`.

## Core API (all endpoints require authentication)

`GET /health`, `GET /system`, `GET /providers`, `POST /providers/select`,
`POST /chat`, `GET|DELETE /history`, `GET /inference/status`,
`POST /inference/unload`, `GET /inference/telemetry`,
`POST /guardian/preview`, `POST /guardian/propose`,
`POST /guardian/submit`, `GET /guardian/candidates/{id}`,
`POST /guardian/review`.
