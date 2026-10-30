# Guardian deployment and operations

Guardian changes a NixOS system only through typed, allowlisted options, root
approval, a signed build attestation and a one-time trial boot. Design and
rationale: [ADR-002](adr/002-guardian-candidate-model.md). Evidence:
[M3](evidence/M3.md), [M4](evidence/M4.md).

## On an installed NexusOS

Guardian is on by default (`nix/profiles/nexusos.nix`), with nothing to set
up. The person sees changes in the desktop's **System change** dialog
(`shell/qml/Guardian.qml`): Nexus, First run or "Keep it" proposes a change;
the person chooses **Approve and stage**; Guardian builds and tests it; the
person enters their password; it is used from the next restart.

| Piece | Runs as | What it may do |
|---|---|---|
| `nexus-guardian` (broker) | root, no capabilities, no network | Decide, as before. Also `describe`: its own record of a caller's checked candidate, for the approval dialog, remembering the verified build |
| `nexus-guardian-keys` | root oneshot, every boot | Make this computer's Ed25519 builder key once (`/var/lib/nexus-guardian-builder/builder.key`, 0600 in a 0700 root directory, never replaced) and write the broker's trusted keys: the configured `trustedBuilderKeys` plus this key |
| `nexus-guardian-builder` | root, no capabilities, no network, writes only its work directory | For a change on the running system: evaluate the host's own configuration with the candidate overlay (`scripts/guardian/candidate-build.nix`), build it through the Nix daemon, boot it once in a throwaway VM (multi-user.target and `staging.requiredUnits` must start), and sign what it saw, including today's value of each changed setting and package. Never activates anything |
| `nexus-guardian-desktop-approve` | root, run only by polkit | Register the same single-use, 120-second approval as the root console, for the person polkit authorised (`PKEXEC_UID`), only for an unstaged candidate they submitted, and bound to the build hash it was given, which must be the one the broker verified |
| `nexus-guardian-stage` | root, no network | Stage the caller's own permitted, unstaged decision (`stage_executor.py`), and report the boot outcome |
| `nexus-guardian-boot-check` | root oneshot, every boot | Confirm or roll back a trial, as before |

### Approval without a root console

The person approves with an administrator's password through polkit, as
designed in [ADR-002](adr/002-guardian-candidate-model.md#desktop-approval):

1. Approve and stage: Core submits the plan to the broker and asks the
   builder to check it (`POST /guardian/proposals/{id}/check`).
2. When the checks pass, the dialog shows the broker's own record of the
   candidate, fetched through Core's broker client
   (`GET /guardian/proposals/{id}/describe`, the broker's `describe` with the
   builder's attestation): each setting's path, today's value (signed by the
   builder) and new value, each package, the risk, the candidate ID and the
   build hash. Core's proposal text is shown only as "Nexus says"; if it names
   different changes, the broker's are shown with a warning and the password
   is not asked for until the person clicks Approve again.
3. The dialog runs
   `pkexec /run/current-system/sw/bin/nexus-guardian-desktop-approve <candidateId> <buildHash>`
   with both values from that record. The polkit action
   `org.nexusos.guardian.approve` is `auth_admin` for the active session only
   (never `_keep`), so every approval asks for the password, and its message
   includes `$(command_line)`, so the prompt names the candidate and build.
   The shell is the session's polkit agent: it shows the password box in the
   dialog only for that action whose message names exactly the dialog's
   candidate and build; any other request (another action, or a Guardian
   approval of something else) gets a plain window showing the action ID and
   message. The password goes to polkit's own helper, not to Core.
4. After the password the helper (as root, `PKEXEC_UID` naming the person)
   re-reads the candidate from the broker: it must exist, have been submitted
   by that person, not be staged, and its verified build must be the hash it
   was given. The approval is bound to that candidate and build.
5. The dialog then asks for the usual review: `POST /guardian/review` with
   the builder's attestation. The broker uses a bound approval only with an
   attestation of that same build, and decides as before.
6. If eligible: `POST /guardian/stage {decision}`, then **Restart now**.

A compromised Core or stolen API token still cannot approve: approving needs
the password at polkit's prompt. Code running as the person cannot get one
change approved while the shell's dialog shows another by rewriting Core's
proposals or starting its own approval: the dialog shows the broker's record,
the prompt names the candidate and build, and the helper and broker check
both. (Code that replaces the shell itself, and so the session's polkit
agent, is not covered by this.) A polkit rule that grants wheel everything without a password
(some people add one) would remove that step; do not add such a rule on a
NexusOS machine. Approvals at the root console (`nexus-guardian approve`)
show the overlay themselves and are not bound to one build.

### Trial boots on systemd-boot

Installed systems boot with systemd-boot. Staging keeps the baseline as the
EFI default (`bootctl set-default`) and makes the candidate's trial-boot entry
a one-shot entry (`bootctl set-oneshot`); entries are found by their exact
`init=` path. The trial-boot entry is the `guardian-trial` specialisation:
the same system with `panic=N boot.panic_on_fail`, so a trial that fails
resets and systemd-boot starts the baseline. The boot check then removes the
EFI default (confirmed: loader.conf's newest entry is the candidate's plain
entry) or restores the baseline profile. `staging.bootloader` picks GRUB or
systemd-boot from the configuration; GRUB's `next_entry` names the same
specialisation ("Guardian trial boot").

Only the trial boot has those parameters. Every other boot, including the
confirmed candidate's later boots, stops at an emergency shell on a stage-1
failure (a file system check that needs repair, say) instead of rebooting.

### Status for the desktop

`GET /guardian/proposals` lists proposals and their state (waiting, checking,
ready, approved, staged, applied, rolled-back, declined, failed), with every
changed item's before and after; the fields are documented at the top of
`shell/qml/Guardian.qml`. `POST /guardian/propose` (Nexus) and
`POST /guardian/proposals` (a typed plan) add one; neither submits anything.

### Security updates

Everything from nixpkgs (the kernel, Firefox, Chromium, the desktop) would
otherwise stay at its install-day version: an update is a Guardian candidate
of its own kind, approved like any change (`nix/modules/updates.nix`;
Flathub apps update separately, daily, in `apps.nix`).

1. **Check** (`nexus-update-check.service`, root; `nexus-update-check.timer`
   weekly with `Persistent` and a random delay of up to a day, at idle CPU
   and I/O priority; after `network-online.target`, and offline it retries
   hourly). It refuses to start with less than `nexusos.updates.minFreeGiB`
   (10 GiB) free on /nix and says so. It copies `/etc/nixos` into
   `/var/lib/nexus-updates/work`, runs `nix flake update nexusos/nixpkgs`
   there (only NexusOS's nixpkgs moves, on the release branch the lock
   already follows, `nixos-26.05`), and accepts the result only if no other
   lock node changed and nixpkgs moved forward on the same branch. The new
   lock goes to `/var/lib/nexus-updates/locks/<sha256>.lock` (0700 root),
   `update.json` names it with the hashes of the old and new lock and the
   nixpkgs revisions and dates, and the fetched source gets a garbage
   collector root until it is built. The work directory is removed.
   Neither `/etc/nixos` nor NexusOS's source is changed.
2. **Propose:** Core polls the builder (`updates`) and records a proposal of
   kind `update` (source `updates`), and sends it for checking at once:
   `submit {description, update: {lockHash}}` and `build {update: {lockHash}}`.
   The hash is all that Core or the person can name; the broker and builder
   accept only the update `update.json` offers now, made from today's
   `/etc/nixos/flake.lock`, and read the lock themselves from the root-owned
   directory, checking its hash.
3. **Build and test:** the candidate is the host configuration with no
   overlay, evaluated from the builder's own copy of `/etc/nixos` with that
   lock (removed after evaluation), then built and VM-tested as usual. The
   builder also refuses under 10 GiB free. The signed statement carries
   `update`: the lock's hash and the previous lock's, the nixpkgs branch and
   revisions before and after (with `lastModified`), and the package versions
   that change (`nix store diff-closures` between the running system and the
   candidate: Linux, Firefox, Chromium and other notable ones first, at most
   16, and how many more). The candidate ID is derived from the lock's hash.
4. **Approve:** `describe` returns `kind: "update"` and that signed record;
   the dialog shows "Security updates: N packages updated, including Linux
   6.18.56 → 6.18.60, …" and the revisions; the top bar says "Security updates
   are ready". The password approval, review and staging are unchanged.
5. **Trial boot:** staging copies the exact lock beside the pending trial
   (`/var/lib/nexus-guardian/staged-flake.lock`). Only a confirmed trial makes
   it `/etc/nixos/flake.lock` (root, 0644, written beside it and renamed), and
   only if that file is still the lock the update was made from
   (`flake-lock-installed`, or `flake-lock-skipped` in `staging.jsonl`). After
   a rollback or fallback `/etc/nixos` is as it was. Later rebuilds, changes
   and update checks build on the installed lock.

Asking Nexus "check for updates" offers a card; on "Do it" Core asks the
builder (`check-updates`, at most every 10 minutes), which writes
`/var/lib/nexus-guardian-builder/work/update-requested`; the root path unit
`nexus-update-check.path` starts the check. No sudo or password is involved.
`GET /guardian/updates` (and Nexus's `get_system_updates`) report the last
check and where the update is. Logs: `journalctl -u nexus-update-check`,
`/var/lib/nexus-updates/status.json`.

## Enable on other NixOS hosts


Import `nexusos.nixosModules.guardian` (see the README) and set:

| Option | Meaning |
|---|---|
| `services.nexus-guardian.allowedUsers` | Users whose Core may submit and review. Root is never a client |
| `services.nexus-guardian.hostConfig` | This host's flake configuration, e.g. `path:/etc/nixos#nixosConfigurations.host` |
| `services.nexus-guardian.trustedBuilderKeys` | Base64 Ed25519 public keys of trusted builders (may be empty with `localBuilder.enable`) |
| `services.nexus-guardian.localBuilder.enable` | This host builds, VM-tests and signs candidates itself, with a key made at first boot |
| `services.nexus-guardian.desktopApproval.enable` | Approval with a password through polkit, and the staging service for the person's own decisions |
| `services.nexus-guardian.staging.enable` | Trial boots (GRUB or systemd-boot, `staging.bootloader`): adds the `guardian-trial` specialisation (the trial-boot entry, with `panic=N boot.panic_on_fail`; no other entry has them), sets `boot.loader.grub.default = "saved"` on GRUB, and enables the boot check |
| `services.nexus-guardian.staging.requiredUnits` | Units that must be active before a trial is confirmed |

**Never** list the lab key in `nix/tests/lab-builder.pub`: it is public.

## Builder key

On the trusted build machine, not on the host being changed:

```bash
nexus-guardian-attest keygen --private-key /secure/path/builder.key   # prints the public key
```

Put the printed public key in `trustedBuilderKeys`. To rotate it, add the
new key, rebuild, then remove the old one.

## Change workflow

1. **Propose (optional):** `POST /guardian/propose {"request": "enable printing"}`
   returns a typed preview from the model. Check it.
2. **Submit:** `POST /guardian/submit {description, changes, packages?}`. The
   broker binds the candidate to the running generation. A package is a plain
   nixpkgs attribute (the pattern is in `schema/guardian-options.json`); it is
   sent as a `{"package": attr}` change and adds `pkgs.<attr>` to
   `environment.systemPackages`. `POST /try/keep {attr}` (`nexus try keep`)
   submits exactly that for one package.
3. **Build and test** the candidate on the builder. It is the host
   configuration with the module overlay added through `extendModules`, then
   evaluated, built and VM-tested.
4. **Attest:**
   `nexus-guardian-attest sign --private-key … --candidate candidate.json --toplevel <out> --drv-path <drv> --builder <name> --evaluate pass --build pass --vm-test pass`
5. **Approve** at a root console:
   `nexus-guardian approve --candidate-id <id> --user <name>`. It shows the
   overlay and risk, and requires typing `APPROVE <id>`. The approval lasts
   120 seconds and works once.
6. **Review:** `POST /guardian/review {candidateId, attestation}` returns
   `eligible` and a decision hash.
7. **Stage (root):** `nexus-guardian-stage --decision <hash>`, then reboot.
   The candidate boots once. `nexus-guardian-boot-check.service` confirms it,
   or restores the baseline.

Logs:

- `journalctl -u nexus-guardian` gives the reason behind each denial.
- `/var/lib/nexus-guardian/staging.jsonl` records the staging and boot outcomes.

## User-data backups

To snapshot data before every staging run, create
`/etc/nexus-guardian/backup.json`:

```json
{"paths": ["/home/alice"], "repository": "/var/lib/nexus-backup", "keep": 5}
```

If the backup fails, nothing is staged. Restore with:

```bash
python3 scripts/backup/nexus_backup.py restore --repo /var/lib/nexus-backup/home_alice --snapshot <name> --target /restore/alice
```

## Lab flakes

- `nix/guardian/`: the original GRUB VM lab, with manual known-good entry
  selection (PR #47).
- `flake.nix` checks:
  - `guardian-broker`: the broker as a hardened service;
  - `nexus-stack`: per-user Core with the broker;
  - `guardian-trial-boot`: unattended trial boot and fallback on a GRUB disk;
  - the `nexus-guardian` and `nexus-core` package builds.
