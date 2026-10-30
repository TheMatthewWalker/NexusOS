# Guardian's local builder (local_builder.py) evaluates this file to check one
# candidate on this computer. The candidate is the host's own NixOS
# configuration with the candidate module added through extendModules
# (ADR-002: an overlay, never a replacement). `vmTest` boots exactly that
# configuration once in a throwaway virtual machine and passes only if it
# reaches multi-user.target with the required units active. `before` reports
# today's value of each changed option, and whether each package is already
# installed, so the person sees before and after.
#
# Every argument is a plain string checked by the builder; `module` is the
# candidate's canonical module text, written to a file by the builder. No
# Nix code is generated from user input. A security update has no module
# (module = ""): `flake` is then the builder's own copy of /etc/nixos with
# the update's flake.lock, so the candidate is the host configuration itself,
# from newer nixpkgs.
{ flake, host, module ? "", requiredUnits ? "", timeoutSeconds ? "1800", options ? "", packages ? "" }:
let
  configuration = (builtins.getFlake flake).nixosConfigurations.${host};
  candidate = configuration.extendModules { modules = if module == "" then [ ] else [ (/. + module) ]; };
  pkgs = candidate.pkgs;
  lib = pkgs.lib;
  words = text: builtins.filter (word: word != "") (lib.splitString " " text);
  units = words requiredUnits;
  attempt = value: let result = builtins.tryEval value; in if result.success then result.value else null;
  current = name: attempt (lib.attrByPath (lib.splitString "." name) null configuration.config);
  installed = attr: let package = lib.attrByPath (lib.splitString "." attr) null configuration.pkgs; in
    attempt (package != null && builtins.elem package configuration.config.environment.systemPackages);
  # Added to the test machine only (virtualisation.vmVariant), never to the candidate itself.
  vmCheck = {
    virtualisation.graphics = false;
    virtualisation.memorySize = 2048;
    virtualisation.diskSize = 4096;
    # The test machine boots without a bootloader, so it needs no trial-boot
    # entry (guardian-trial); leaving it out saves a second evaluation.
    specialisation = lib.mkForce { };
    systemd.services.nexus-guardian-vm-check = {
      description = "Guardian: report whether the candidate started in its test machine";
      wantedBy = [ "multi-user.target" ];
      after = [ "multi-user.target" ];
      path = [ pkgs.systemd pkgs.coreutils ];
      serviceConfig.Type = "oneshot";
      script = ''
        result=PASS
        for unit in multi-user.target ${lib.escapeShellArgs units}; do
          waited=0
          until systemctl is-active --quiet "$unit"; do
            waited=$((waited + 5))
            if [ "$waited" -gt 600 ]; then
              echo "nexus-guardian-vm-check: $unit did not start" > /dev/ttyS0
              result=FAIL
              break
            fi
            sleep 5
          done
        done
        echo "NEXUS-VM-CHECK-$result" > /dev/ttyS0
        systemctl poweroff --no-block
      '';
    };
  };
  testMachine = (candidate.extendModules { modules = [ { virtualisation.vmVariant = vmCheck; } ]; }).config.system.build.vm;
  before = { options = lib.genAttrs (words options) current; packages = lib.genAttrs (words packages) installed; };
  toplevel = candidate.config.system.build.toplevel;
  vmTest = pkgs.runCommand "nexus-guardian-vm-test" { requiredSystemFeatures = [ "kvm" ]; } ''
    export TMPDIR=$(mktemp -d) USE_TMPDIR=1
    export NIX_DISK_IMAGE=$TMPDIR/test.qcow2
    cd "$TMPDIR"
    timeout ${timeoutSeconds} ${testMachine}/bin/run-*-vm < /dev/null > console.log 2>&1 || true
    tail -n 40 console.log
    grep -q NEXUS-VM-CHECK-PASS console.log
    echo pass > $out
  '';
in
{
  inherit before toplevel vmTest;
  # What the builder evaluates first: both derivations and today's values, in one go.
  evaluation = { toplevel = toplevel.drvPath; vmTest = vmTest.drvPath; inherit before; };
}
