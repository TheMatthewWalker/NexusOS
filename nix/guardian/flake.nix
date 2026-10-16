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
      nixosConfigurations.guardian-broken-generation = nixpkgs.lib.nixosSystem {
        inherit system;
        modules = [ ./bootloader-vm.nix ./bootloader-broken.nix ];
      };
      packages.${system} = {
        guardian-broken-generation =
          self.nixosConfigurations.guardian-broken-generation.config.system.build.toplevel;
        guardian-bootloader-vm =
          self.nixosConfigurations.guardian-bootloader-lab.config.system.build.vmWithBootLoader;
      };
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
      guardian-bootloader-rollback = pkgs.testers.runNixOSTest {
        name = "nexus-guardian-bootloader-rollback";
        nodes.machine = { lib, ... }: {
          system.stateVersion = "26.05";
          virtualisation.useBootLoader = true;
          virtualisation.diskImage = "./guardian-rollback.qcow2";
          virtualisation.memorySize = 2048;
          boot.loader.grub.enable = true;
          boot.loader.grub.device = "/dev/vda";
          boot.loader.timeout = 8;
          boot.loader.grub.extraConfig = ''
            serial --unit=0 --speed=115200
            terminal_input console serial
            terminal_output console serial
          '';
          boot.kernelParams = [ "console=ttyS0,115200n8" ];
          system.extraDependencies = [
            self.nixosConfigurations.guardian-broken-generation.config.system.build.toplevel
          ];
          systemd.services.nexus-known-good = {
            wantedBy = [ "multi-user.target" ];
            serviceConfig.Type = "oneshot";
            script = "echo known-good > /run/nexus-generation";
          };
        };
        testScript = ''
          with subtest("Boot real GRUB baseline"):
              machine.start(allow_reboot=True)
              machine.wait_for_unit("multi-user.target")
              machine.wait_for_file("/run/nexus-generation")
              baseline = machine.succeed("readlink -f /run/current-system").strip()
              assert baseline.startswith("/nix/store/")
          with subtest("Stage second NixOS generation for next boot"):
              broken = "${self.nixosConfigurations.guardian-broken-generation.config.system.build.toplevel}"
              machine.succeed("test -x " + broken + "/bin/switch-to-configuration")
              machine.succeed("nix-env --profile /nix/var/nix/profiles/system --set " + broken)
              machine.succeed(broken + "/bin/switch-to-configuration boot")
              assert machine.succeed("readlink -f /run/current-system").strip() == baseline
              assert machine.succeed("readlink -f /nix/var/nix/profiles/system").strip() == broken
          with subtest("Observe intentionally failed candidate boot"):
              machine.reboot()
              machine.wait_for_console_text("Timed out waiting for device /dev/vda1", timeout=160)
          with subtest("Select known-good GRUB generation and recover"):
              machine.send_monitor_command("system_reset")
              machine.wait_for_console_text("GNU GRUB", timeout=45)
              machine.send_key("down", delay=0.25)
              machine.send_key("ret", delay=0.25)
              machine.send_key("down", delay=0.25)
              machine.send_key("ret", delay=0.25)
              machine.wait_for_unit("multi-user.target", timeout=120)
              machine.succeed("grep -qx known-good /run/nexus-generation")
              assert machine.succeed("readlink -f /run/current-system").strip() == baseline
              machine.succeed("nix-env --profile /nix/var/nix/profiles/system --set " + baseline)
              assert machine.succeed("readlink -f /nix/var/nix/profiles/system").strip() == baseline
              machine.succeed(baseline + "/bin/switch-to-configuration boot")
          with subtest("Verify recovered baseline survives a normal reboot"):
              machine.reboot()
              machine.wait_for_unit("multi-user.target", timeout=120)
              machine.succeed("grep -qx known-good /run/nexus-generation")
              assert machine.succeed("readlink -f /run/current-system").strip() == baseline
        '';
      };
      };
    };
}
