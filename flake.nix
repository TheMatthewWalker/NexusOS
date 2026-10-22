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
        nexus-try = pkgs.callPackage ./nix/packages/nexus-try.nix { };
        default = self.packages.${system}.nexus-core;
      };

      # Headless Wayland harness for the SpaceUI shell spike (docs/design/spaceui-spike.md):
      # a wlroots compositor, screenshots, both candidate shell stacks and Mesa's
      # llvmpipe so GPU shaders run without a GPU.
      devShells.${system}.spaceui-spike = pkgs.mkShell {
        packages = with pkgs; [
          sway grim jq foot glslang quickshell qt6.qtshadertools mesa
          (python3.withPackages (ps: [ ps.pygobject3 ps.pycairo ps.pillow ps.pywayland ]))
          wlr-protocols libxkbcommon
          gtk4 gtk4-layer-shell webkitgtk_6_0 gobject-introspection glib-networking
          nodejs_22 esbuild
        ];
        SPIKE_MESA = "${pkgs.mesa}";
        SPIKE_WAYLAND_XML = "${pkgs.wayland-scanner.out}/share/wayland/wayland.xml";
        SPIKE_WLR_PROTOCOLS = "${pkgs.wlr-protocols}/share/wlr-protocols";
        SPIKE_LAYER_SHELL = "${pkgs.gtk4-layer-shell}/lib/libgtk4-layer-shell.so";
      };

      nixosModules = {
        guardian = ./nix/modules/guardian.nix;
        core = ./nix/modules/core.nix;
        try-apps = ./nix/modules/try-apps.nix;
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
        nexus-try = self.packages.${system}.nexus-try;
        try-apps = import ./nix/tests/try-apps.nix {
          inherit pkgs;
          tryAppsModule = self.nixosModules.try-apps;
        };
      };
    };
}
