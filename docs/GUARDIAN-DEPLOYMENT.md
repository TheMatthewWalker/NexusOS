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
