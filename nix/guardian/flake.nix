{
  description = "NexusOS Guardian NixOS VM safety test";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
    in {
      nixosConfigurations.guardian-bootloader-lab = nixpkgs.lib.nixosSystem {
        inherit system;
        modules = [ ./bootloader-vm.nix ];
      };
      packages.${system}.guardian-bootloader-vm =
        self.nixosConfigurations.guardian-bootloader-lab.config.system.build.vmWithBootLoader;
      checks.${system} = {
        guardian-boot = pkgs.testers.runNixOSTest {
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
      guardian-recovery = pkgs.testers.runNixOSTest {
        name = "nexus-guardian-staged-recovery";
        nodes.machine = { pkgs, ... }: {
          system.stateVersion = "26.05";
          systemd.services.nexus-staged-broken = {
            serviceConfig.Type = "oneshot";
            script = "exit 1";
          };
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
          machine.succeed("grep -qx baseline-ready /run/nexus-known-good")
          # Simulate a broken staged service, without touching bootloader or activation.
          machine.succeed("systemctl start nexus-staged-broken.service >/dev/null 2>&1 || true")
          machine.succeed("systemctl is-failed nexus-staged-broken.service")
          machine.succeed("grep -qx baseline-ready /run/nexus-known-good")
          machine.succeed("systemctl is-active multi-user.target")
        '';
      };
      };
    };
}
