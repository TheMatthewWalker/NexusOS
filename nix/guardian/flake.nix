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
          boot.loader.timeout = 45;
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
              # Parse the generated GRUB configuration while the guest is healthy.
              # Select the menuentry whose Linux init argument names the exact
              # known-good store path, rather than assuming a menu position.
              import re
              grub_cfg = machine.succeed("cat /boot/grub/grub.cfg")
              entries = []
              active = []
              depth = 0
              for line in grub_cfg.splitlines():
                  match = re.match(r"^\s*(menuentry|submenu)\s+(['\"])(.*?)\2.*\{\s*$", line)
                  if match:
                      parent = active[-1] if active else None
                      siblings = entries if parent is None else parent["children"]
                      entry = dict(kind=match.group(1), label=match.group(3),
                                   index=len(siblings), depth=depth,
                                   children=[], has_baseline=False)
                      siblings.append(entry)
                      active.append(entry)
                  if active and baseline in line and re.search(r"\binit=", line):
                      active[-1]["has_baseline"] = True
                  depth += line.count("{") - line.count("}")
                  while active and depth <= active[-1]["depth"]:
                      active.pop()
              def locate(entries, prefix=()):
                  for entry in entries:
                      selection = prefix + (entry["index"],)
                      if entry["kind"] == "menuentry" and entry["has_baseline"]:
                          return selection
                      result = locate(entry["children"], selection)
                      if result is not None:
                          return result
                  return None
              known_good_selection = locate(entries)
              assert known_good_selection is not None, (
                  "No GRUB menuentry boots the exact known-good generation")
              assert len(known_good_selection) <= 3, "Unexpected GRUB menu nesting"
              print("GRUB KNOWN-GOOD SELECTION: " + str(known_good_selection), flush=True)
          with subtest("Observe intentionally failed candidate boot"):
              import re
              import time
              # Snapshot before reboot: only evidence emitted by this boot counts.
              offset = len(machine.get_console_log())
              # A reboot() call waits for the guest backdoor to reconnect.
              # A deliberately unbootable system never reconnects; use QEMU
              # keyboard reset and serial-only observation instead.
              machine.send_key("ctrl-alt-delete")
              deadline = time.monotonic() + 145
              last_progress = 0
              boot_identity = False
              failure_seen = False
              failure_patterns = [
                  r"Timed out waiting for device",
                  r"Dependency failed for (?:/sysroot|Initrd Root|Initrd File)",
                  r"Failed to (?:mount|start).*?(?:root|sysroot)",
                  r"Kernel panic",
                  r"You are in emergency mode",
              ]
              while time.monotonic() < deadline:
                  output = machine.get_console_log()[offset:]
                  transcript = re.sub(r"\x1b\[[0-9;:]*[A-Za-z]", "", output)
                  # The kernel command line identifies the generation selected by GRUB.
                  boot_identity = boot_identity or (
                      broken in transcript and
                      "Kernel command line:" in transcript
                  )
                  failure_seen = failure_seen or any(
                      re.search(pattern, transcript, re.IGNORECASE)
                      for pattern in failure_patterns
                  )
                  if boot_identity and failure_seen:
                      print("SERIAL EVIDENCE: broken generation reached failed boot state", flush=True)
                      break
                  if time.monotonic() - last_progress > 10:
                      print("SERIAL WATCH: bytes=" + str(len(output)) + " identity=" + str(boot_identity) + " failure=" + str(failure_seen), flush=True)
                      last_progress = time.monotonic()
                  time.sleep(2)
              assert boot_identity, "Serial log never proved GRUB started the broken generation"
              assert failure_seen, "No boot failure evidence in the broken generation serial log"
          with subtest("Select known-good GRUB generation and recover"):
              # This is a host QEMU monitor command, not a guest-shell command.
              recovery_offset = len(machine.get_console_log())
              machine.send_monitor_command("system_reset")
              # Do not use the consumptive wait_for_console_text queue: GRUB
              # output may arrive before the waiter begins consuming it.
              grub_deadline = time.monotonic() + 45
              while time.monotonic() < grub_deadline:
                  if "Welcome to GRUB!" in machine.get_console_log()[recovery_offset:]:
                      break
                  time.sleep(0.25)
              else:
                  raise AssertionError("GRUB menu not observed after QEMU reset")
              # The welcome banner precedes GRUB's interactive menu rendering.
              # Give firmware and menu initialization time to accept key input.
              time.sleep(2)
              # The indices come from parsing grub.cfg above, not from a
              # hard-coded assumption about the order of NixOS generations.
              for index in known_good_selection:
                  machine.send_key("home", delay=0.3)
                  for _ in range(index):
                      machine.send_key("down", delay=0.2)
                  machine.send_key("ret", delay=0.5)
              # Confirm that GRUB selected the old kernel using host-side
              # serial evidence before attempting any guest backdoor call.
              recovery_deadline = time.monotonic() + 90
              recovered_kernel = False
              while time.monotonic() < recovery_deadline:
                  recovery_log = machine.get_console_log()[recovery_offset:]
                  if baseline in recovery_log and "Kernel command line:" in recovery_log:
                      recovered_kernel = True
                      break
                  time.sleep(2)
              assert recovered_kernel, "GRUB never selected the known-good generation"
              machine.wait_for_unit("multi-user.target", timeout=75)
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
