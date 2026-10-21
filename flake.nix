{
  description = "NexusOS: AI-native Linux — Guardian packages, NixOS modules and VM tests";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
    in {
      packages.${system} = {
        nexus-guardian = pkgs.callPackage ./nix/packages/nexus-guardian.nix { };
        nexus-core = pkgs.callPackage ./nix/packages/nexus-core.nix { };
        default = self.packages.${system}.nexus-core;
      };

      nixosModules = {
        guardian = ./nix/modules/guardian.nix;
        core = ./nix/modules/core.nix;
      };

      checks.${system} = {
        nexus-guardian = self.packages.${system}.nexus-guardian;
        guardian-broker = import ./nix/tests/guardian-broker.nix {
          inherit pkgs;
          guardianModule = self.nixosModules.guardian;
        };
        nexus-stack = import ./nix/tests/nexus-stack.nix {
          inherit pkgs;
          guardianModule = self.nixosModules.guardian;
          coreModule = self.nixosModules.core;
        };
        nexus-core = self.packages.${system}.nexus-core;
        guardian-trial-boot = import ./nix/tests/guardian-trial-boot.nix {
          inherit pkgs;
          guardianModule = self.nixosModules.guardian;
        };
      };
    };
}
