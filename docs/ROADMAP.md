# NexusOS — executable project roadmap

**Baseline:** 2026-10-14. **Cadence:** milestone-based, not promised delivery dates. **Status terminology:** TODO / IN PROGRESS / DONE / BLOCKED. A milestone becomes DONE only after its acceptance criteria have evidence in CI, tests, and (where required) real hardware.

## Release gates

| Gate | Milestone | Acceptance criteria | Status |
|---|---|---|---|
| M0 | Repository/bootstrap | Build on Ubuntu in GitHub CI; test project runs; README reproduces local setup | DONE — CI verified |
| M1 | Nexus Core v0.1 | Health, provider listing/selection, read-only system inventory, local Ollama chat, cloud OpenAI-compatible chat; CLI returns meaningful errors | PARTIAL — mock CI passed; physical Ollama/cloud test open |
| M2 | Hardened Core v0.2 | Authenticated IPC, per-user storage, OS-keyring credentials, retention, request limits and provider isolation | IN PROGRESS — SQLite migration and Linux Unix SO_PEERCRED checks merged; live keyring, multi-user, network-egress and security acceptance still open |
| M3 | Guardian preview | Typed plans, policy, Nix diff, approval, privileged immutable audit, no shell | IN PROGRESS — allowlisted typed previews and review acknowledgement with local hash-linked audit; advisory diff and one-time review tokens verified (#30, #31); privileged audit and executor open |
| M4 | Guardian rollback | Reproducible Nix builds, VM smoke checks, staged boot, known-good recovery demonstrated on test hardware | IN PROGRESS — isolated GRUB-backed staged-broken-generation rollback and subsequent normal reboot passed in Guardian NixOS VM #41 (PR #47); privileged staging, data backup and physical hardware recovery remain open |
| M5 | Inference manager | llama.cpp adapter, KV budget, VRAM/RAM monitoring, eviction/idle sleep and application priority | IN PROGRESS — llama.cpp API route, Ollama unload, optional idle eviction, Linux/NVIDIA telemetry and benchmark harness CI verified; actual offloading, KV budget and hardware tests open |
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
- **M4:** Pinned disposable NixOS VM boot, candidate verification, fail-closed review and read-only generation inspection implemented. Remaining: privileged staging, recovery testing and user-data backups. Guardian cannot apply changes.
- **M5:** Backend availability and explicit Ollama unload request plus budget arithmetic exist. Remaining: llama.cpp lifecycle, VRAM/RAM telemetry, KV budgeting, idle eviction, foreground preemption and benchmarks on real GPUs.

No milestone M2–M5 is DONE until its own definition-of-done criteria are satisfied. No desktop/UI work begins before these are addressed.


## Evidence update — 2026-10-14

Merged into `main` during development: Guardian preview and audit (#6, #8), Linux Secret Service credentials (#9), RAM/VRAM telemetry (#10), strict Guardian change validation (#11), opt-in idle unloading (#12), pinned NixOS VM boot/smoke test (#14), local llama.cpp provider (#15), response-size limits (#16), and benchmark harness (#18). GitHub Actions has verified their associated builds/tests, but mock-backend tests do not establish real model performance. Unix-only IPC is tracked in #19 until its CI and merge gates are satisfied.

Remaining implementation needed before M2–M5 can be marked DONE:
- M2: secure IPC rollout and peer credentials, real keyring integration testing, multi-user isolation, endpoint DNS/egress hardening, request concurrency and sensitive-data logging checks.
- M3: policy-enforcing privileged broker, protected audit storage, real Nix module evaluation/diff and confirmation tied to a one-time authorisation.
- M4: staged NixOS generation deployment, automated boot health assessment, known-good rollback including failed boot tests, and a separate user-data backup path.
- M5: llama.cpp process lifecycle and GPU/CPU offload allocation, KV-cache/context budgeting, application foreground preemption, GPU vendor coverage and real 8/24GB benchmark data.

The policy remains: do not claim these milestones complete merely because CI is green; hardware acceptance is a separately tracked gate. M6 Desktop/UI remains out of scope.


## Current acceptance update — 2026-10-15

- **M2:** Provider settings/history, Linux Secret Service lookup, bearer-authenticated optional Unix IPC, request size, concurrency/rate/response limits, host allowlists, and explicit cloud egress opt-in are merged and CI-tested. Open: native peer credentials for privileged services, SQLite history migration, unlocked-keyring integration, robust DNS/egress controls and hardening review.
- **M3:** Typed allowlisted previews, advisory before/after diff (#30), single-use two-minute review acknowledgement (#31), and hash-linked **user-writable** audit records exist. None constitutes trusted privileged approval or immutable audit. Remaining: independent policy broker, observed NixOS configuration diff and protected audit.
- **M4:** Pinned NixOS guest boots the verified allowlisted candidate; fail-closed review policy and read-only active-generation inspection exist. Remaining: privileged staging, reboot health checks, deliberately broken-candidate rollback in disposable VM and on later hardware, and separate data backup/recovery. No host activation is available.
- **M5:** Ollama and llama.cpp API routing, optional idle unload, first-NVIDIA VRAM/RAM telemetry, configured output/context token budgets (#26), local latency benchmark and inference concurrency exist. Remaining: managed process lifecycle, measured GPU offloading and KV usage, foreground preemption, AMD/Intel support and CPU/8GB/24GB hardware benchmarks.

**Completion policy:** Keep M2–M5 marked IN PROGRESS until their security and actual-system gates pass. The pinned test VM does not prove hardware rollback or GPU behaviour. Desktop/UI stays out of scope.

### M2 security work in progress — PR #33 (not merged)

- Cloud endpoint guard now rejects literal IP addresses even when explicitly listed as cloud hosts; xUnit regression cases added for IPv4, IPv6, mapped IPv6, metadata and loopback addresses.
- This is **not** DNS rebinding protection: hostname resolution is not pinned to the outbound connection. CI, live DNS behavior, NixOS VM and hardware validation are not yet evidenced for this PR. M2 remains IN PROGRESS.

## M2 DNS hardening PR #33 — acceptance evidence (2026-10-15)

- Cloud HTTP handler validates every resolved DNS address and connects to a selected validated IP; automatic redirects and proxies remain disabled. Literal IP cloud endpoints are rejected.
- Deterministic unit tests cover public/private mixed DNS answer order, empty answers, selected public address and special-use IPv4/IPv6 rejection. This is **unit/mock-level evidence**, not an external DNS, proxy or live cloud integration test.
- CI evidence: Nexus Core CI #88 and Guardian Validation #60 passed on `8c660ce8`. Subsequent commits require their own checks before merge.
- **NixOS VM:** DNS egress behavior has not been independently validated in a VM. **Physical hardware:** no live cloud, GPU or rollback validation performed.
- M2 stays **IN PROGRESS**: native IPC peer identity, SQLite migration, keyring integration and broader egress threat review remain open. M3–M5 also remain IN PROGRESS; no Desktop/UI work is authorized.

## M2 cloud connection regression follow-up — PR #34 (2026-10-15)

- **Mock/unit gate:** PR #34 extends fail-closed mixed DNS-answer tests and checks literal-IP/empty hostname rejection at the connection-policy entry point.
- **CI:** Initial Nexus Core CI #93 **failed** during test-project compilation (CS1729: unsupported `SocketsHttpConnectionContext` constructor). Guardian Validation #65 passed. Tests were revised to exercise a dedicated hostname guard; revised-head CI must pass before merging.
- **NixOS VM gate:** No DNS interception/egress test has been performed in the NixOS VM for this change. Existing pinned NixOS boot smoke tests are a different gate.
- **Hardware/live-network gate:** No live cloud provider, DNS rebinding, GPU or physical rollback test has been performed. No claims of physical validation.
- **Remaining M2 gates:** native Unix IPC peer credentials, per-user SQLite persistence, unlocked keyring integration and stronger end-to-end network egress validation. M2–M5 remain IN PROGRESS; no Desktop/UI work.

## M2 IPv6 egress follow-up — 2026-10-15

- Follow-up to merged DNS pinning PRs #33 and #34: deny 6to4 `2002::/16` and IPv6 documentation `3fff::/20` destinations. Both are inappropriate for cloud model endpoint egress; tests include 6to4 addresses embedding public and private IPv4.
- **Mock/unit evidence:** targeted address-policy tests are committed; CI is pending for this branch. No live DNS or network egress test has been performed.
- **NixOS VM evidence:** no VM egress validation on this change. **Hardware-only gates:** physical cloud network, GPU and rollback checks remain unperformed.
- M2–M5 remain IN PROGRESS; Desktop/UI is out of scope.

## M2 malformed-history regression — 2026-10-15

- PR #36 was superseded by merged PR #37; the current `IsRecentEntry` filter already removes malformed JSONL records from count-based retention. The isolated regression test is ported to main without overwriting PR #37 age-based filtering.
- **Mock/unit:** deterministic test committed; CI pending. **NixOS VM:** no new VM test performed. **Physical hardware:** no hardware checks performed. SQLite migration and native IPC peer authentication remain outstanding; M2–M5 IN PROGRESS.


## Acceptance-gate refresh — 2026-10-20 (after PR #47)

The status table above supersedes older dated progress snapshots below; those snapshots are retained as historical evidence, not current implementation claims.

### M2 — next security acceptance gates

- [x] SQLite-backed per-user provider/history state and transactional legacy JSONL migration implemented; plaintext legacy backups remain and must be handled separately.
- [x] Unix-domain Core IPC verifies kernel `SO_PEERCRED` UID and requires bearer authentication; TCP development mode is a separate loopback-only boundary.
- [ ] **Keyring integration:** in a real Linux user session, prove unlocked-keyring success, locked/unavailable-keyring fail-closed behaviour and no credential logging. Record reproducible commands and test evidence.
- [ ] **Multi-user isolation:** exercise distinct Linux UIDs against each other's socket, token, state directory and SQLite files; prove access denied and no cross-user history disclosure.
- [ ] **Persistence/retention:** exercise SQLite migration restart/idempotence, malformed records, permissions, age/limit expiry, clear-history and retained legacy backup handling.
- [ ] **Egress and abuse:** exercise DNS answer changes, denied private/special-use addresses, redirects/proxies, oversized responses, cancellation, concurrency and sensitive-data log inspection against an integration environment.
- [ ] Complete security review and document evidence per gate before declaring M2 DONE.

### M3 — next broker integration gates (after M2 security boundary)

- [x] Non-executing typed previews, one-time review acknowledgement, root-owned review-broker and approval-issuer prototypes exist.
- [ ] Package the review-only broker as an isolated systemd/NixOS VM service and test ownership, `SO_PEERCRED`, restart, denial and resource limits.
- [ ] Separate trusted attestation signing from verification; bind approvals to candidate hash, observed generation, authorised UID and expiry, with durable nonce consumption.
- [ ] Persist protected approval, denial and outcome audit records; test replay, forgery, stale baselines, filesystem replacement and crash recovery.
- [ ] Independently review privilege boundaries before implementing any staging executor. No host activation until these gates pass.

### M4 — newly verified VM gate and remaining acceptance

- [x] GRUB-backed disposable VM boots baseline, stages a deliberately broken generation, observes failed boot, selects original generation from parsed GRUB entries, verifies exact recovered kernel init, reconnects to healthy userspace, restores the default profile and survives another reboot. **Evidence:** PR #47, Guardian NixOS VM workflow #41, commit `70b8ae4`.
- [ ] Authorised, narrowly scoped staging executor and trusted build provenance (depends on M3).
- [ ] Separate user-data backup/restore, interruption/power-loss scenarios and documented recovery drills.
- [ ] Physical hardware boot-failure recovery and health checks. Passing CI/VM is not hardware acceptance.

**Evidence rule:** Every unchecked gate requires a reproducible test procedure, captured passing result and security review where applicable. M2–M5 remain IN PROGRESS; M6 desktop work is not authorised by these VM results.
