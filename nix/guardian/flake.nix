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
          services.openssh.enable = true;
          system.stateVersion = "26.05";
        };
        testScript = ''
          machine.start()
          machine.wait_for_unit("multi-user.target")
          machine.wait_for_unit("sshd.service")
          machine.succeed("systemctl is-active sshd.service")
        '';
      };
    };
}
