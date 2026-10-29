# The NexusOS system profile: what every installed NexusOS machine runs.
# The installer (nix/installer) writes a small flake that imports this
# profile plus the machine's own hardware-configuration.nix and local.nix.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos;
in
{
  imports = [ ../modules/desktop.nix ../modules/nvidia.nix ];

  options.nexusos = {
    user = lib.mkOption {
      type = lib.types.str;
      description = "The person's login name. The installer sets it in local.nix.";
    };
    fullName = lib.mkOption {
      type = lib.types.str;
      default = "";
      description = "Shown on the login screen.";
    };
    autologin = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Log straight into the desktop at boot. Off by default: the login prompt asks for the password.";
    };
    localModel.pullOnFirstBoot = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Download the default local model (services.nexus-core.ollamaModel) once
        the machine is online. Until it is present, Nexus says the model is
        missing and apps keep working.
      '';
    };
  };

  config = {
    system.nixos.distroName = "NexusOS";
    system.nixos.distroId = "nexusos";

    boot.loader.systemd-boot.enable = lib.mkDefault true;
    boot.loader.efi.canTouchEfiVariables = lib.mkDefault true;
    boot.loader.systemd-boot.configurationLimit = lib.mkDefault 20;
    hardware.enableRedistributableFirmware = lib.mkDefault true;
    hardware.graphics.enable = true;

    networking.hostName = lib.mkDefault "nexus";
    time.timeZone = lib.mkDefault "UTC";
    i18n.defaultLocale = lib.mkDefault "en_GB.UTF-8";

    nix.settings.experimental-features = [ "nix-command" "flakes" ];
    nix.gc = { automatic = true; dates = "weekly"; options = "--delete-older-than 30d"; };

    users.users.${cfg.user} = {
      isNormalUser = true;
      description = cfg.fullName;
      extraGroups = [ "wheel" "networkmanager" "video" "audio" ];
    };
    security.sudo.wheelNeedsPassword = true;

    services.nexus-desktop = {
      enable = true;
      autologinUser = if cfg.autologin then cfg.user else null;
      greeter = { user = cfg.user; fullName = cfg.fullName; };
    };

    # Local inference. The model itself is downloaded once the machine is online.
    services.ollama.enable = true;
    systemd.services.nexus-model-pull = lib.mkIf cfg.localModel.pullOnFirstBoot {
      description = "Download the default Nexus local model";
      wantedBy = [ "multi-user.target" ];
      after = [ "ollama.service" "network-online.target" ];
      wants = [ "network-online.target" ];
      requires = [ "ollama.service" ];
      environment.OLLAMA_HOST = "127.0.0.1:11434";
      serviceConfig = {
        Type = "oneshot";
        Restart = "on-failure";
        RestartSec = 60;
      };
      script = ''
        # A model copied from the installer image (nexus-install): move it into
        # Ollama's models directory, owned by Ollama, before deciding to download.
        seed=/var/lib/nexus-model-seed
        if [ -d "$seed/manifests" ]; then
          models=${lib.escapeShellArg config.services.ollama.models}
          owner=$(stat -L -c %u:%g ${lib.escapeShellArg config.services.ollama.home})
          mkdir -p "$models"
          cp -r --no-preserve=ownership,mode "$seed/." "$models/"
          chown -R "$owner" "$models"
          rm -rf "$seed"
          echo "Moved the model from the installer into $models"
        fi
        model=${lib.escapeShellArg config.services.nexus-core.ollamaModel}
        if ${config.services.ollama.package}/bin/ollama show "$model" > /dev/null 2>&1; then
          echo "$model is already present"
        else
          ${config.services.ollama.package}/bin/ollama pull "$model"
        fi
      '';
    };

    environment.systemPackages = with pkgs; [ git vim curl ];
    system.stateVersion = lib.mkDefault "26.05";
  };
}
