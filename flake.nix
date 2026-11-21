{
  description = "NexusOS: AI-native Linux — Guardian packages, NixOS modules and VM tests";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
      ollamaModel = pkgs.callPackage ./nix/packages/ollama-model.nix { };
      # The local models the installer image carries, once pinned with
      # `nix run .#lock-model [-- name:tag]` (nix/models/*.json): one per
      # kind of computer (qwen3:8b with a graphics card, qwen3:4b without).
      # The installer copies the one chosen for the computer; a model not on
      # the image downloads after installation.
      modelDir = ./nix/models;
      modelLocks = pkgs.lib.optionals (builtins.pathExists modelDir)
        (map (f: modelDir + "/${f}") (builtins.filter (pkgs.lib.hasSuffix ".json") (builtins.attrNames (builtins.readDir modelDir))));
      bundledModel = if modelLocks == [ ] then null
        else pkgs.symlinkJoin { name = "nexus-models"; paths = map ollamaModel.fromLock modelLocks; };
      # What each computer's initrd is made from (the start-up screen's theme, plugins
      # and font, the unlock prompt's keymaps...). They are copied into the initrd, so
      # they are not in the finished system's closure; the image carries them so that
      # installing builds that initrd with no internet.
      # What each computer's system-path is made from: every output of every
      # installed package that it takes (the package's own outputs plus man,
      # info and doc), not only those the finished system keeps. system-path
      # is rebuilt on each install (it holds per-computer commands), and an
      # output missing from the image would mean building that package, and
      # all it is built with, from source with no internet.
      referencePackageOutputs = pkgs.lib.concatMap (name: let
          c = self.nixosConfigurations.${name}.config;
          outputsOf = p: p.outputs or [ "out" ];
          wanted = p: pkgs.lib.unique (builtins.filter (o: builtins.elem o (outputsOf p))
            ((p.meta.outputsToInstall or [ (builtins.head (outputsOf p)) ]) ++ c.environment.extraOutputsToInstall));
        in pkgs.lib.concatMap (p: map (o: p.${o}) (wanted p)) c.environment.systemPackages)
        [ "nexus-reference" "nexus-reference-nvidia" ];
      # Likewise what other per-computer parts copy files from rather than
      # refer to: udev's rules and hardware database (from every udev package,
      # such as sane's net.conf), and the session's environment, whose values
      # name store paths (CUPS_DATADIR). A file naming them keeps them on the image.
      # CUPS's program tree (cups-progs) is a buildEnv that Nix always builds
      # where it is installed, never copies: it needs each of its packages'
      # installed outputs (Ghostscript's man pages too) on the image.
      outputsToInstall = p: let outs = p.outputs or [ "out" ]; in
        map (o: p.${o}) (builtins.filter (o: builtins.elem o outs) (p.meta.outputsToInstall or [ (builtins.head outs) ]));
      referenceCopiedInputs = pkgs.lib.concatMap (name: let
          s = self.nixosConfigurations.${name};
          c = s.config;
        in c.services.udev.packages ++ [ (pkgs.writeText "nexusos-${name}-environment.json"
            (builtins.toJSON (c.environment.sessionVariables // c.environment.variables))) ]
          ++ pkgs.lib.optionals c.services.printing.enable (pkgs.lib.concatMap outputsToInstall
            ([ c.services.printing.package s.pkgs.ghostscript s.pkgs.cups-filters s.pkgs.libcupsfilters ]
             ++ pkgs.lib.optional c.services.printing.browsed.enable c.services.printing.browsed.package
             ++ c.services.printing.drivers)))
        [ "nexus-reference" "nexus-reference-nvidia" ];
      referenceInitrdInputs = referencePackageOutputs ++ referenceCopiedInputs ++ pkgs.lib.concatMap (name: let s = self.nixosConfigurations.${name}.config.boot.initrd.systemd; in
          map (p: if builtins.isAttrs p then p.source else p) (builtins.filter (p: !(builtins.isAttrs p) || p.enable) s.storePaths)
          ++ builtins.filter (x: x != null) (map (c: if c.enable or true then c.source or null else null) (builtins.attrValues s.contents)))
        [ "nexus-reference" "nexus-reference-nvidia" ];
      # Where installed systems follow NexusOS's releases (update_check.py): this
      # repository's release branch. An image built from a commit (a clean tree)
      # records it, and the installer pins the installed system to it.
      nexusRelease = { owner = "TheMatthewWalker"; repo = "NexusOS"; ref = "release"; };
      installerWith = model: nixpkgs.lib.nixosSystem {
        inherit system;
        specialArgs = {
          nexusSource = self;
          nixpkgsSource = nixpkgs.outPath;
          referenceSystem = self.nixosConfigurations.nexus-reference.config.system.build.toplevel;
          referenceSystemNvidia = self.nixosConfigurations.nexus-reference-nvidia.config.system.build.toplevel;
          inherit referenceInitrdInputs nexusRelease;
          bundledModel = model;
        };
        modules = [ ./nix/installer/iso.nix ];
      };
    in {
      packages.${system} = {
        nexus-guardian = pkgs.callPackage ./nix/packages/nexus-guardian.nix { };
        nexus-core = pkgs.callPackage ./nix/packages/nexus-core.nix { };
        nexus-shell = pkgs.callPackage ./nix/packages/nexus-shell.nix { };
        nexus-install = pkgs.callPackage ./nix/packages/nexus-install.nix { };
        # The installer image: `nix build .#iso`, then write result/iso/*.iso to a USB stick.
        iso = self.nixosConfigurations.installer.config.system.build.isoImage;
        # `nix build .#iso-slim`: the installer image without the local AI model.
        iso-slim = self.nixosConfigurations.installer-slim.config.system.build.isoImage;
        nexus-try = pkgs.callPackage ./nix/packages/nexus-try.nix { };
        colibri = pkgs.callPackage ./nix/packages/colibri.nix { };
        nexusos-plymouth-theme = pkgs.callPackage ./nix/packages/nexus-plymouth.nix { };
        default = self.packages.${system}.nexus-core;
      } // pkgs.lib.optionalAttrs (bundledModel != null) {
        bundled-model = bundledModel;
      };

      # `nix run .#lock-model [-- name:tag]` pins a model for the installer image.
      apps.${system}.lock-model = {
        type = "app";
        program = "${pkgs.writeShellScript "lock-model" ''
          PATH=${pkgs.git}/bin:$PATH exec ${pkgs.python3}/bin/python3 ${./scripts/models/lock-model.py} "$@"
        ''}";
      };

      # lib.mkSystem builds a NexusOS machine from the profile plus the
      # machine's own modules; the flake that nexus-install writes uses it.
      lib.mkSystem = { modules ? [ ] }: nixpkgs.lib.nixosSystem {
        inherit system;
        modules = [
          self.nixosModules.nexusos
          # Pin `nixpkgs` to the source this system was built from, and keep
          # that source on disk, so the machine can rebuild itself offline.
          { nix.registry.nixpkgs.flake = nixpkgs; nix.nixPath = [ "nixpkgs=${nixpkgs}" ]; }
        ] ++ modules;
      };

      nixosConfigurations = {
        nexus-reference = self.lib.mkSystem { modules = [ ./nix/installer/reference.nix ]; };
        # The same with the NVIDIA driver, so installing on an NVIDIA machine
        # finds the driver already built on the installer image.
        nexus-reference-nvidia = self.lib.mkSystem {
          modules = [ ./nix/installer/reference.nix { nexusos.nvidia.enable = true; } ];
        };
        installer = installerWith bundledModel;
        # The slim download: the same installer without the local AI model,
        # which then downloads once the installed computer is online.
        installer-slim = installerWith null;
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
            nodejs_22 esbuild rsync dash
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
        nexusos = ./nix/profiles/nexusos.nix;
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
        nexus-shell = self.packages.${system}.nexus-shell;
        # The installer image and the installed system evaluate (option
        # conflicts between NexusOS and the installer profile show here),
        # without building the 5 GB image.
        iso-evaluates = pkgs.writeText "iso-evaluates" ''
          ${builtins.unsafeDiscardStringContext self.packages.${system}.iso.drvPath}
          ${builtins.unsafeDiscardStringContext self.nixosConfigurations.nexus-reference.config.system.build.toplevel.drvPath}
        '';
        # Hyprland's own parser checks the generated configuration (window
        # rules, binds and options change between Hyprland releases).
        hyprland-config = let
          cfg = self.nixosConfigurations.nexus-reference.config;
          conf = cfg.environment.etc."nexus/hyprland.conf".source;
        in pkgs.runCommand "hyprland-config-check" { } ''
          export HOME=$TMPDIR XDG_RUNTIME_DIR=$TMPDIR/run
          mkdir -m 700 $XDG_RUNTIME_DIR
          ${cfg.programs.hyprland.package}/bin/Hyprland --verify-config --config ${conf} > log 2>&1 || true
          sed -n '/Config parsing result/,$p' log
          grep -qx 'config ok' log
          touch $out
        '';
        hyprland-desktop = import ./nix/tests/hyprland-desktop.nix {
          inherit pkgs;
          desktopModule = self.nixosModules.desktop;
        };
        login-screen = import ./nix/tests/login-screen.nix {
          inherit pkgs;
          desktopModule = self.nixosModules.desktop;
        };
        wifi-picker = import ./nix/tests/wifi-picker.nix {
          inherit pkgs;
          desktopModule = self.nixosModules.desktop;
        };
        nexus-installer = import ./nix/tests/nexus-installer.nix {
          inherit pkgs referenceInitrdInputs nexusRelease;
          nexusSource = self;
          nixpkgsSource = nixpkgs.outPath;
          referenceSystem = self.nixosConfigurations.nexus-reference.config.system.build.toplevel;
          nexusInstall = self.packages.${system}.nexus-install;
        };
        guardian-trial-boot = import ./nix/tests/guardian-trial-boot.nix {
          inherit pkgs;
          guardianModule = self.nixosModules.guardian;
        };
        nexus-try = self.packages.${system}.nexus-try;
        try-apps = import ./nix/tests/try-apps.nix {
          inherit pkgs nixpkgs;
          tryAppsModule = self.nixosModules.try-apps;
        };
      };
    };
}
