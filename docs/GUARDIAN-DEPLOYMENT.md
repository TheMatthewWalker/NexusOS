# Guardian Nix validation prototype

This deliberately narrow tool validates the **schema and generated Nix expression**, not an actual NixOS deployment. It accepts only four boolean module options and has no privileged operations, disk writes to system paths, NixOS activation or rollback.

Run:

```bash
python3 -m unittest discover -s tests/guardian -v
echo '[{"option":"services.printing.enable","enabled":true}]' > changes.json
python3 scripts/guardian/validate.py --changes changes.json
```

Next stage: pinned NixOS flake, evaluated derivations using `nix eval` / `nix build`, VM boot and service checks, then a separately privileged and authorisation-controlled deployment service with known-good recovery. A successful syntax check **does not establish boot safety**.


## Isolated NixOS boot test (experimental)

The `nix/guardian/flake.nix` test boots a disposable NixOS 26.05 VM and verifies that a deliberately configured service starts. The `Guardian NixOS VM` workflow runs this in GitHub Actions, independently of Core CI. This verifies a *test configuration* only: it does not stage a user's actual machine configuration, prove that their display drivers work, confirm bootloader rollback, or test a failing candidate recovery.

**Dependency pinning:** The NixOS 26.05 nixpkgs revision is now recorded in `nix/guardian/flake.lock`, so CI uses a pinned dependency graph. This still does not demonstrate staged deployment or bootloader rollback.


## Candidate manifest (M4 headless groundwork)

To prepare an immutable-by-content *identifier* for a candidate without performing any system modification, run:

```bash
python3 scripts/guardian/candidate.py --changes changes.json --baseline generation-42 --output candidate.json
```

This creates a deterministic hash-based candidate ID for the validated Nix module and the claimed known-good baseline, with the only permitted next stages listed as `evaluate`, `build`, and `vm-test`. The manifest explicitly marks activation as `prohibited`; it does **not** establish that the baseline actually exists, that the manifest is signed or tamper-proof, or that a rollback can be performed. A later privileged, policy-enforcing executor must revalidate all fields, verify the system generation and approval, and stage or activate only after independent tests. No host system change is authorised by this manifest.


## Candidate-backed VM validation (M4)

The pinned Guardian test VM imports `nix/guardian/generated-candidate.nix`. CI reconstructs the candidate from `candidate-changes.json` and the declared baseline, validates the complete manifest, generates the Nix module independently, and compares it byte-for-byte with the module imported by the VM. Only then does the disposable NixOS VM test run. This connects the tested VM configuration to the verified change proposal instead of merely booting a separate hardcoded configuration. It **does not** validate that a real system baseline generation exists, stage a host deployment, or prove a bootloader fallback; the privileged deployment and recovery pathway remains unfinished.


## Fail-closed promotion review gate (M4)

`scripts/guardian/recovery_gate.py` independently reconstructs a candidate and checks that a separate evidence document names the same candidate ID and baseline, all three requested checks (`evaluate`, `build`, `vm-test`) are explicitly `true`, and the provided current baseline still matches. Missing, failed, unexpected or stale checks result in **ineligible for review**. CI deliberately supplies a failed-build fixture and confirms it is rejected.

This is a **nonprivileged prototype**, not an approval or deployment authorisation mechanism. The evidence JSON and baseline argument are supplied by the caller and are **not cryptographically trusted**. It must never be used by itself to grant root access or perform NixOS activation; a separate privileged executor must obtain trusted CI/build attestations, query the active generation itself, enforce explicit user approval and provide tested rollback.


## Read-only active generation inspection (M4)

Run `python3 scripts/guardian/baseline_inspect.py` on a NixOS host to observe whether `/run/current-system` and `/nix/var/nix/profiles/system` are symlinks resolving to the same Nix store path. Missing links, non-store targets and divergence fail closed. A mismatch can be legitimate after staging or switching generations; investigate it before deployment. The checker never edits profiles, executes commands or grants approval. It is an **observation**, not a cryptographically trusted privileged attestation: a future independent broker must verify the real state and provenance at decision time.


## Independent policy verification prototype (M3)

`scripts/guardian/broker_policy.py` is a **pure, non-executing** policy verifier. It requires matching candidate and evidence identities, an HMAC-SHA256 evidence authenticator, an explicitly marked single-use approval record, a matching independently supplied baseline identifier, and passing evaluation/build/VM checks. Negative unit tests reject forged signatures, unsigned failures, stale baselines and malformed approvals.

**Security boundary:** this module is **not a privileged broker**. No secure key provisioning, proof of trusted CI origin, protected append-only audit, verified OS generation observation, persistent nonce consumption or privilege separation is provided by this code. A caller can fabricate a signed attestation if they hold the key; an `approval.singleUse` field by itself does not stop replay. The verifier does not stage, switch, boot or roll back NixOS. Production use must wait for a separately isolated root-owned broker with key isolation, one-time authorization state, kernel-authenticated callers and tested host rollback.


## Root-owned review broker groundwork (M3, non-deploying)

`scripts/guardian/root_review_broker.py` introduces an isolated **review-only** service implementation. On the intended Linux deployment it must run under a dedicated root-managed service: its state directory must be root-owned mode 0700; its 32-byte HMAC signing key and SQLite approval database must be root-owned mode 0600. The key is never returned by the socket service. It accepts requests only from explicitly configured Linux UIDs verified using kernel `SO_PEERCRED`, independently checks the active system/profile generation via `baseline_inspect.inspect`, verifies signed build evidence and strict Guardian candidate policy, and transactionally consumes short-lived approval nonces before recording a read-only decision. Replay is denied even after process restart. Unit tests use the CI process UID and an isolated temporary directory; **they do not run a root service**.

**Deliberately unavailable:** The socket protocol has only `review`, returning `eligibleForReview` and `executed:false`. There is no `stage`, `switch`, `boot`, arbitrary command execution, shell bridge or activation permission. Registration of approvals is available only as an internal method for a future root-controlled trusted approval issuer—not via the socket. Likewise a trusted CI attestation issuer and secure distribution/provisioning of its verification key still need design and integration. No user-facing service installation is supplied. This stage must not be exposed as a production privileged broker.

**Security limitations:** The reference implementation is not yet race-free against directory/file replacement, does not verify the candidate derivation against a trusted builder, and lacks an OS service manager sandbox and a separate operator approval path. Approval registration, key rotation, audit immutability, denial logging, socket cleanup and resource exhaustion need further hardening. Explicit provisioning of a 32-byte protected key is required; never store it in the source repository. All real deployment operations remain blocked pending VM rollback exercises and an independent security review.
