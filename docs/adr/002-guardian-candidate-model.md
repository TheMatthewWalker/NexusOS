# ADR-002 — Guardian candidate model, attestation and approval

**Status:** accepted (2026-10-21). **Supersedes:** the C#/Python split
plan IDs, HMAC evidence and Core-side `/guardian/acknowledge`.

## Context

Before this decision, Guardian had two disconnected implementations:

- C# `GuardianPlanner` and Python `validate.py` each kept their own copy of
  the option allowlist and computed different IDs.
- Build "evidence" was a set of booleans signed with a shared HMAC key, so
  anyone able to verify it could also forge it, and nothing tied it to an
  actual built system.
- CI used a symbolic baseline (`generation-42`) that could never match the
  `/nix/store/...` path the broker observes.
- Core recorded approvals in a user-writable file, and any holder of the API
  token could approve its own request.

## Decision

1. **One schema.** `schema/guardian-options.json` is the only option
   allowlist. C# embeds it; Python reads it. `schema/guardian-vectors.json`
   holds cross-language test vectors that both test suites must reproduce
   byte for byte.

2. **A plan is the change; a candidate is the change applied to an observed
   system.**
   - `moduleText` is rendered canonically: options sorted, two-space indent,
     trailing newline.
   - `planId = sha256("nexus-guardian-plan-v1\n" + moduleText)[:24]`.
   - A candidate is an **overlay**: the generated module is added to the
     host's own NixOS configuration with `extendModules`. It never replaces
     that configuration. Its identity binds the observed running system and
     the host configuration reference:
     `candidateId = sha256("nexus-guardian-candidate-v1\n" + baseline + "\n" + hostConfig + "\n" + moduleText)[:24]`.
   - `baseline` must be a `/nix/store/<hash>-nixos-system-*` path, and must
     equal the broker's own observation of `/run/current-system` at review
     time. Symbolic baselines are rejected.

3. **Attestations are Ed25519 signatures from the builder, bound to
   outputs.**
   - The builder (CI or a local trusted build service) signs a canonical
     JSON statement: `candidateId`, `baseline`, `toplevel` (the built system
     store path), `drvPath`, the `evaluate`/`build`/`vm-test` results,
     `builder` and `issuedAt`.
   - The broker holds **public keys only** (root-owned, not writable by
     anyone else). It also checks that `toplevel` exists in the local store
     and, where a deriver query is available, that `toplevel` was produced
     by `drvPath`.

4. **Approval is a root decision recorded by the broker.**
   - `root_approval_issuer.py issue` (interactive root console only)
     registers a single-use, expiring approval for one `(candidateId, uid)`
     pair in the broker's root-owned SQLite ledger.
   - Review consumes it transactionally, and every decision is recorded,
     hash-chained, in that same root-owned ledger.
   - Core never records approvals. It only forwards requests over the
     broker socket and shows the results.

5. **Separation of privilege.**
   - **Core** (user) turns intent into typed plans.
   - **The broker** (root, no capabilities, no network) observes the system,
     verifies, and decides.
   - **The staging executor** (root, separate unit) runs only
     `switch-to-configuration boot` for a toplevel the broker approved, so
     the change takes effect at next boot and never alters the running
     system.

   A failed boot falls back to the previous generation via boot counting.

## Consequences

- A compromised Core, or a stolen user API token, can at most *request* a
  review. It cannot approve, forge evidence or stage anything.
- Leaking a builder's private key enables forged attestations, but approval
  still requires root, and staging still requires the exact toplevel.
  Rotate a key by removing it from `trustedBuilderKeys`.
- Hosts must be defined by a flake so `hostConfig` is reproducible. The
  VM lab uses `nix/guardian#nixosConfigurations.guardian-bootloader-lab`.
- Still open, and tracked: verifying TTY presence against a compromised
  root account.

## Desktop approval

**Added 2026-10-29.** A person at their own computer approves without a root
console, with the least new privilege:

- **Approval is polkit's.** `nexus-guardian-desktop-approve` runs only as the
  polkit action `org.nexusos.guardian.approve` (`auth_admin`, active session,
  never remembered). It registers exactly the console's approval: one
  candidate, one UID (polkit's `PKEXEC_UID`, never root), 120 seconds, used
  up by the next review, and only for a candidate that UID submitted. The
  broker's review, ledger and checks are unchanged.
- **The approval names what is approved** (added 2026-10-30). The dialog
  shows the broker's own record of the candidate (`describe`: settings with
  today's value as the builder signed it and the new value, packages, risk,
  candidate ID, build hash), not Core's user-writable text. The helper takes
  `<candidateId> <buildHash>`, polkit's message shows that command line, and
  the shell's agent answers in the dialog only for that action naming that
  candidate and build. After the password the helper requires the build to
  be the one the broker last verified for that candidate, unstaged, and binds
  the approval to it: the review may use it only with that build's
  attestation.
- **The builder is local.** Each computer makes its own Ed25519 key at first
  boot (root only). A separate root service with no network builds the
  overlay through the Nix daemon, boots it in a VM and signs the result; the
  broker still holds public keys only and checks store paths and derivers.
  A stolen local key still cannot approve or stage.
- **Staging is asked for, not granted.** A staging service stages only the
  caller's own permitted, unstaged decision, once. It cannot create a
  permitted decision.
- **Core records, never decides.** Core keeps the proposals the dialog shows
  and forwards requests; it cannot approve (no password), review in its own
  favour (the broker decides), or stage anything not already permitted.

## Security updates

An overlay cannot express "move to newer nixpkgs", so a second candidate kind
does: `update`, the host configuration with no overlay, evaluated with a
newer `flake.lock` in which only NexusOS's nixpkgs moved forward on its
release branch. Its candidate ID is derived from the baseline, the host
configuration and the lock's SHA-256; the lock is written by a root service
(`nexus-update-check`) to a root-owned directory and read from there by hash
only, never from a path a caller names. The attestation binds the lock hash
and signs what the person is shown (nixpkgs revisions, changed package
versions). Approval, review and the trial boot are unchanged; the lock
becomes `/etc/nixos/flake.lock` only after a confirmed trial boot. Details:
docs/GUARDIAN-DEPLOYMENT.md, "Security updates".
