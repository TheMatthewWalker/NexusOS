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
| `nexus-guardian` (broker) | root, no capabilities, no network | Decide, as before. Unchanged |
| `nexus-guardian-keys` | root oneshot, every boot | Make this computer's Ed25519 builder key once (`/var/lib/nexus-guardian-builder/builder.key`, 0600 in a 0700 root directory, never replaced) and write the broker's trusted keys: the configured `trustedBuilderKeys` plus this key |
| `nexus-guardian-builder` | root, no capabilities, no network, writes only its work directory | For a change on the running system: evaluate the host's own configuration with the candidate overlay (`scripts/guardian/candidate-build.nix`), build it through the Nix daemon, boot it once in a throwaway VM (multi-user.target and `staging.requiredUnits` must start), and sign what it saw. Never activates anything |
| `nexus-guardian-desktop-approve` | root, run only by polkit | Register the same single-use, 120-second approval as the root console, for the person polkit authorised (`PKEXEC_UID`) and only for a candidate they submitted |
| `nexus-guardian-stage` | root, no network | Stage the caller's own permitted, unstaged decision (`stage_executor.py`), and report the boot outcome |
| `nexus-guardian-boot-check` | root oneshot, every boot | Confirm or roll back a trial, as before |

### Approval without a root console

The person approves with an administrator's password through polkit, as
designed in [ADR-002](adr/002-guardian-candidate-model.md#desktop-approval):

1. Approve and stage: Core submits the plan to the broker and asks the
   builder to check it (`POST /guardian/proposals/{id}/check`).
2. When the checks pass, the dialog runs
   `pkexec /run/current-system/sw/bin/nexus-guardian-desktop-approve <candidateId>`.
   The polkit action `org.nexusos.guardian.approve` is `auth_admin` for the
   active session only (never `_keep`), so every approval asks for the
   password. The shell is the session's polkit agent, so the password is
   typed in the dialog; it goes to polkit's own helper, not to Core.
3. The dialog then asks for the usual review: `POST /guardian/review` with
   the builder's attestation. The broker decides, as before.
4. If eligible: `POST /guardian/stage {decision}`, then **Restart now**.

A compromised Core or stolen API token still cannot approve: approving needs
the password at polkit's prompt. A polkit rule that grants wheel everything
without a password (some people add one) would remove that step; do not
add such a rule on a NexusOS machine.

### Trial boots on systemd-boot

Installed systems boot with systemd-boot. Staging keeps the baseline as the
EFI default (`bootctl set-default`) and makes the candidate a one-shot entry
(`bootctl set-oneshot`); entries are found by their exact `init=` path. With
`panic=N boot.panic_on_fail`, a trial that fails resets and systemd-boot
starts the baseline. The boot check then removes the EFI default (confirmed:
loader.conf's newest entry is the candidate) or restores the baseline profile.
`staging.bootloader` picks GRUB or systemd-boot from the configuration.

Note: with `boot.panic_on_fail`, any failed boot resets instead of stopping
at an emergency shell; earlier versions stay in the boot menu.

### Status for the desktop

`GET /guardian/proposals` lists proposals and their state (waiting, checking,
ready, approved, staged, applied, rolled-back, declined, failed), with every
changed item's before and after; the fields are documented at the top of
`shell/qml/Guardian.qml`. `POST /guardian/propose` (Nexus) and
`POST /guardian/proposals` (a typed plan) add one; neither submits anything.

## Enable on other NixOS hosts


Import `nexusos.nixosModules.guardian` (see the README) and set:

| Option | Meaning |
|---|---|
| `services.nexus-guardian.allowedUsers` | Users whose Core may submit and review. Root is never a client |
| `services.nexus-guardian.hostConfig` | This host's flake configuration, e.g. `path:/etc/nixos#nixosConfigurations.host` |
| `services.nexus-guardian.trustedBuilderKeys` | Base64 Ed25519 public keys of trusted builders (may be empty with `localBuilder.enable`) |
| `services.nexus-guardian.localBuilder.enable` | This host builds, VM-tests and signs candidates itself, with a key made at first boot |
| `services.nexus-guardian.desktopApproval.enable` | Approval with a password through polkit, and the staging service for the person's own decisions |
| `services.nexus-guardian.staging.enable` | Trial boots (GRUB or systemd-boot, `staging.bootloader`): sets `panic=N boot.panic_on_fail` (and `boot.loader.grub.default = "saved"` on GRUB), and enables the boot check |
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
