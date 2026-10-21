# Roadmap history (superseded snapshots)

These dated progress notes were written during development from 14 to 20
October 2026. They are kept as a historical record. They are **not** current
status: see [the roadmap](../ROADMAP.md) and the per-milestone evidence files
in this folder.

## Evidence and outstanding gates (2026-10-14)

- **M0:** GitHub CI builds and tests source on Ubuntu.
- **M1:** Mock Ollama end-to-end chat path works; an actual downloaded model and paid cloud API remain **unverified**.
- **M2:** Per-user bearer authentication, persistent settings/history, deletion, basic rate limiting and provider URL restrictions exist. Remaining: OS-keyring secrets, Unix socket peer credentials, bounded provider responses, multi-user boundaries, comprehensive logging/egress protections.
- **M3:** Typed allowlisted Nix preview, explicit non-executing acknowledgement and hash-linked user-file audit are implemented. Remaining: trusted privileged broker, protected audit store, real Nix diff and policy enforcement.
- **M4:** Pinned disposable NixOS VM boot, candidate verification, fail-closed review and read-only generation inspection implemented. Remaining: privileged staging, recovery testing and user-data backups. Guardian cannot apply changes.
- **M5:** Backend availability and explicit Ollama unload request plus budget arithmetic exist. Remaining: llama.cpp lifecycle, VRAM/RAM telemetry, KV budgeting, idle eviction, foreground preemption and benchmarks on real GPUs.

No milestone M2–M5 is DONE until its own definition-of-done criteria are satisfied. No desktop/UI work begins before these are addressed.


### Evidence update — 2026-10-14

Merged into `main` during development: Guardian preview and audit (#6, #8), Linux Secret Service credentials (#9), RAM/VRAM telemetry (#10), strict Guardian change validation (#11), opt-in idle unloading (#12), pinned NixOS VM boot/smoke test (#14), local llama.cpp provider (#15), response-size limits (#16), and benchmark harness (#18). GitHub Actions has verified their associated builds/tests, but mock-backend tests do not establish real model performance. Unix-only IPC is tracked in #19 until its CI and merge gates are satisfied.

Remaining implementation needed before M2–M5 can be marked DONE:
- M2: secure IPC rollout and peer credentials, real keyring integration testing, multi-user isolation, endpoint DNS/egress hardening, request concurrency and sensitive-data logging checks.
- M3: policy-enforcing privileged broker, protected audit storage, real Nix module evaluation/diff and confirmation tied to a one-time authorisation.
- M4: staged NixOS generation deployment, automated boot health assessment, known-good rollback including failed boot tests, and a separate user-data backup path.
- M5: llama.cpp process lifecycle and GPU/CPU offload allocation, KV-cache/context budgeting, application foreground preemption, GPU vendor coverage and real 8/24GB benchmark data.

The policy remains: do not claim these milestones complete merely because CI is green; hardware acceptance is a separately tracked gate. M6 Desktop/UI remains out of scope.


### Current acceptance update — 2026-10-15

- **M2:** Provider settings/history, Linux Secret Service lookup, bearer-authenticated optional Unix IPC, request size, concurrency/rate/response limits, host allowlists, and explicit cloud egress opt-in are merged and CI-tested. Open: native peer credentials for privileged services, SQLite history migration, unlocked-keyring integration, robust DNS/egress controls and hardening review.
- **M3:** Typed allowlisted previews, advisory before/after diff (#30), single-use two-minute review acknowledgement (#31), and hash-linked **user-writable** audit records exist. None constitutes trusted privileged approval or immutable audit. Remaining: independent policy broker, observed NixOS configuration diff and protected audit.
- **M4:** Pinned NixOS guest boots the verified allowlisted candidate; fail-closed review policy and read-only active-generation inspection exist. Remaining: privileged staging, reboot health checks, deliberately broken-candidate rollback in disposable VM and on later hardware, and separate data backup/recovery. No host activation is available.
- **M5:** Ollama and llama.cpp API routing, optional idle unload, first-NVIDIA VRAM/RAM telemetry, configured output/context token budgets (#26), local latency benchmark and inference concurrency exist. Remaining: managed process lifecycle, measured GPU offloading and KV usage, foreground preemption, AMD/Intel support and CPU/8GB/24GB hardware benchmarks.

**Completion policy:** Keep M2–M5 marked IN PROGRESS until their security and actual-system gates pass. The pinned test VM does not prove hardware rollback or GPU behaviour. Desktop/UI stays out of scope.

### M2 security work in progress — PR #33 (not merged)

- Cloud endpoint guard now rejects literal IP addresses even when explicitly listed as cloud hosts; xUnit regression cases added for IPv4, IPv6, mapped IPv6, metadata and loopback addresses.
- This is **not** DNS rebinding protection: hostname resolution is not pinned to the outbound connection. CI, live DNS behavior, NixOS VM and hardware validation are not yet evidenced for this PR. M2 remains IN PROGRESS.

### M2 DNS hardening PR #33 — acceptance evidence (2026-10-15)

- Cloud HTTP handler validates every resolved DNS address and connects to a selected validated IP; automatic redirects and proxies remain disabled. Literal IP cloud endpoints are rejected.
- Deterministic unit tests cover public/private mixed DNS answer order, empty answers, selected public address and special-use IPv4/IPv6 rejection. This is **unit/mock-level evidence**, not an external DNS, proxy or live cloud integration test.
- CI evidence: Nexus Core CI #88 and Guardian Validation #60 passed on `8c660ce8`. Subsequent commits require their own checks before merge.
- **NixOS VM:** DNS egress behavior has not been independently validated in a VM. **Physical hardware:** no live cloud, GPU or rollback validation performed.
- M2 stays **IN PROGRESS**: native IPC peer identity, SQLite migration, keyring integration and broader egress threat review remain open. M3–M5 also remain IN PROGRESS; no Desktop/UI work is authorized.

### M2 cloud connection regression follow-up — PR #34 (2026-10-15)

- **Mock/unit gate:** PR #34 extends fail-closed mixed DNS-answer tests and checks literal-IP/empty hostname rejection at the connection-policy entry point.
- **CI:** Initial Nexus Core CI #93 **failed** during test-project compilation (CS1729: unsupported `SocketsHttpConnectionContext` constructor). Guardian Validation #65 passed. Tests were revised to exercise a dedicated hostname guard; revised-head CI must pass before merging.
- **NixOS VM gate:** No DNS interception/egress test has been performed in the NixOS VM for this change. Existing pinned NixOS boot smoke tests are a different gate.
- **Hardware/live-network gate:** No live cloud provider, DNS rebinding, GPU or physical rollback test has been performed. No claims of physical validation.
- **Remaining M2 gates:** native Unix IPC peer credentials, per-user SQLite persistence, unlocked keyring integration and stronger end-to-end network egress validation. M2–M5 remain IN PROGRESS; no Desktop/UI work.

### M2 IPv6 egress follow-up — 2026-10-15

- Follow-up to merged DNS pinning PRs #33 and #34: deny 6to4 `2002::/16` and IPv6 documentation `3fff::/20` destinations. Both are inappropriate for cloud model endpoint egress; tests include 6to4 addresses embedding public and private IPv4.
- **Mock/unit evidence:** targeted address-policy tests are committed; CI is pending for this branch. No live DNS or network egress test has been performed.
- **NixOS VM evidence:** no VM egress validation on this change. **Hardware-only gates:** physical cloud network, GPU and rollback checks remain unperformed.
- M2–M5 remain IN PROGRESS; Desktop/UI is out of scope.

### M2 malformed-history regression — 2026-10-15

- PR #36 was superseded by merged PR #37; the current `IsRecentEntry` filter already removes malformed JSONL records from count-based retention. The isolated regression test is ported to main without overwriting PR #37 age-based filtering.
- **Mock/unit:** deterministic test committed; CI pending. **NixOS VM:** no new VM test performed. **Physical hardware:** no hardware checks performed. SQLite migration and native IPC peer authentication remain outstanding; M2–M5 IN PROGRESS.


### Acceptance-gate refresh — 2026-10-20 (after PR #47)

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
