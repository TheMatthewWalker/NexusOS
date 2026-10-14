# Guardian Nix validation prototype

This deliberately narrow tool validates the **schema and generated Nix expression**, not an actual NixOS deployment. It accepts only four boolean module options and has no privileged operations, disk writes to system paths, NixOS activation or rollback.

Run:

```bash
python3 -m unittest discover -s tests/guardian -v
echo '[{"option":"services.printing.enable","enabled":true}]' > changes.json
python3 scripts/guardian/validate.py --changes changes.json
```

Next stage: pinned NixOS flake, evaluated derivations using `nix eval` / `nix build`, VM boot and service checks, then a separately privileged and authorisation-controlled deployment service with known-good recovery. A successful syntax check **does not establish boot safety**.
