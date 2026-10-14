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

**Important:** The flake uses the 26.05 branch without a committed lockfile yet. Until the nixpkgs revision is pinned in `flake.lock`, it is not reproducible across time and must not be considered an M4 release gate.
