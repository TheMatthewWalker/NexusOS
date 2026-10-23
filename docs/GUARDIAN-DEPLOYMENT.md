# Guardian deployment and operations

Guardian changes a NixOS system only through typed, allowlisted options, root
approval, a signed build attestation and a one-time trial boot. Design and
rationale: [ADR-002](adr/002-guardian-candidate-model.md). Evidence:
[M3](evidence/M3.md), [M4](evidence/M4.md).

## Enable

Import `nexusos.nixosModules.guardian` (see the README) and set:

| Option | Meaning |
|---|---|
| `services.nexus-guardian.allowedUsers` | Users whose Core may submit and review. Root is never a client |
| `services.nexus-guardian.hostConfig` | This host's flake configuration, e.g. `path:/etc/nixos#nixosConfigurations.host` |
| `services.nexus-guardian.trustedBuilderKeys` | Base64 Ed25519 public keys of trusted builders |
| `services.nexus-guardian.staging.enable` | GRUB trial boots: sets `boot.loader.grub.default = "saved"` and `panic=N boot.panic_on_fail`, and enables the boot check |
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
