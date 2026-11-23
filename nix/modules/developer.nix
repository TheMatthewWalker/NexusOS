# For developers, on every NexusOS computer:
#  - per-project tools: direnv (with nix-direnv) loads a project's flake
#    development shell when you enter its folder; `nexus-dev init LANGUAGE`
#    writes one (Python, Node, Rust, Go, Java, .NET, C/C++);
#  - containers without root: Podman's per-person socket, with DOCKER_HOST
#    pointing at it, so `docker`, `docker compose` and tools that talk to
#    Docker (VS Code Dev Containers, Testcontainers) work;
#  - SSH keys held by the login keyring (gcr-ssh-agent), so a key's
#    passphrase is asked once; Settings, Developer creates and copies keys;
#  - USB serial boards (Arduino, ESP32, Raspberry Pi Pico) for the person
#    (dialout), and Android phones for adb (android-tools);
#  - virtual machines (KVM, QEMU, virt-manager), off until turned on in
#    Settings, Developer, through Guardian (nexusos.developer.virtualMachines).
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.developer;
  dev = pkgs.writeShellApplication {
    name = "nexus-dev";
    runtimeInputs = with pkgs; [ coreutils git direnv ];
    text = builtins.readFile ../../scripts/desktop/nexus-dev.sh;
  };
in
{
  options.nexusos.developer = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Project tools (direnv, nexus-dev), rootless Docker-compatible containers, SSH keys in the keyring, serial boards.";
    };
    virtualMachines.enable = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Virtual machines: KVM with libvirt, QEMU and virt-manager (Windows or other systems in a window).";
    };
  };

  config = lib.mkIf cfg.enable (lib.mkMerge [
    {
      programs.direnv = { enable = lib.mkDefault true; nix-direnv.enable = lib.mkDefault true; };
      environment.systemPackages = [ dev pkgs.android-tools pkgs.openssh ];

      # Podman's socket for each person, and Docker clients pointed at it.
      systemd.user.sockets.podman.wantedBy = [ "sockets.target" ];
      environment.extraInit = ''
        if [ -z "''${DOCKER_HOST:-}" ] && [ -n "''${XDG_RUNTIME_DIR:-}" ]; then
          export DOCKER_HOST="unix://$XDG_RUNTIME_DIR/podman/podman.sock"
        fi
      '';

      services.gnome.gcr-ssh-agent.enable = lib.mkDefault true;
      programs.ssh.startAgent = lib.mkForce false;

      users.users.${config.nexusos.user}.extraGroups = [ "dialout" ];
    }
    (lib.mkIf cfg.virtualMachines.enable {
      virtualisation.libvirtd.enable = true;
      programs.virt-manager.enable = true;
      users.users.${config.nexusos.user}.extraGroups = [ "libvirtd" "kvm" ];
    })
  ]);
}
