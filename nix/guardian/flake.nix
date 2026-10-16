{
  description = "NexusOS Guardian NixOS VM safety test";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
    in {
      checks.${system}.guardian-boot = pkgs.testers.runNixOSTest {
        name = "nexus-guardian-boot";
        nodes.machine = { pkgs, ... }: {
          imports = [ ./generated-candidate.nix ];
          system.stateVersion = "26.05";
        };
        testScript = ''
          machine.start()
          machine.wait_for_unit("multi-user.target")
          machine.wait_for_unit("sshd.service")
          machine.succeed("systemctl is-active sshd.service")
        '';
      };
      checks.${system}.guardian-recovery = pkgs.testers.runNixOSTest {
        name = "nexus-guardian-staged-recovery";
        nodes.machine = { pkgs, ... }: {
          system.stateVersion = "26.05";
          systemd.services.nexus-known-good = {
            wantedBy = [ "multi-user.target" ];
            serviceConfig.Type = "oneshot";
            script = "echo baseline-ready > /run/nexus-known-good";
          };
        };
        testScript = ''
          machine.start()
          machine.wait_for_unit("multi-user.target")
          machine.wait_for_file("/run/nexus-known-good")
          machine.succeed("test "$(cat /run/nexus-known-good)" = baseline-ready")
          # Simulate a broken staged service, without touching bootloader or activation.
          machine.succeed("systemd-run --unit=nexus-staged-broken --wait /bin/false >/dev/null 2>&1 || true")
          machine.succeed("systemctl is-failed nexus-staged-broken.service")
          machine.succeed("test "$(cat /run/nexus-known-good)" = baseline-ready")
          machine.succeed("systemctl is-active multi-user.target")
        '';
      };
    };
}
