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
        nexus-shell = pkgs.callPackage ./nix/packages/nexus-shell.nix { };
        default = self.packages.${system}.nexus-core;
      };

      # `desktop` builds and tests the Nexus shell (shell/); `spaceui-spike` is the
      # headless Wayland harness from the shell spike (docs/design/spaceui-spike.md):
      # a wlroots compositor, screenshots, both candidate shell stacks and Mesa's
      # llvmpipe so GPU shaders run without a GPU.
      devShells.${system} = rec {
        desktop = spaceui-spike;
        spaceui-spike = pkgs.mkShell {
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
      };

      nixosModules = {
        guardian = ./nix/modules/guardian.nix;
        core = ./nix/modules/core.nix;
        desktop = ./nix/modules/desktop.nix;
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
        nexus-shell = self.packages.${system}.nexus-shell;
        hyprland-desktop = import ./nix/tests/hyprland-desktop.nix {
          inherit pkgs;
          desktopModule = self.nixosModules.desktop;
        };
        guardian-trial-boot = import ./nix/tests/guardian-trial-boot.nix {
          inherit pkgs;
          guardianModule = self.nixosModules.guardian;
        };
      };
    };
}
