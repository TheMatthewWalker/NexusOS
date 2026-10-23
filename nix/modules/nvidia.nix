# NVIDIA graphics for NexusOS. nexus-install detects the card and writes the
# options to local.nix; the installer image has a boot entry that turns it on.
#
# - The proprietary driver, with kernel modesetting (Hyprland needs it).
# - Laptops with a second, built-in GPU use PRIME offload: the desktop runs on
#   the built-in GPU and apps use the NVIDIA one when asked (`nvidia-offload
#   <app>`), which keeps the battery life of the built-in GPU.
# - The local model runs on the NVIDIA GPU through Ollama's Vulkan backend,
#   which the NixOS cache provides prebuilt. CUDA is often faster, but the
#   cache does not carry it, so it compiles on the machine (a long build).
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.nvidia;
  offload = cfg.prime.nvidiaBusId != null && (cfg.prime.intelBusId != null || cfg.prime.amdgpuBusId != null);
  busId = lib.types.nullOr (lib.types.strMatching "PCI:[0-9]+:[0-9]+:[0-9]+");
in
{
  options.nexusos.nvidia = {
    enable = lib.mkEnableOption "the NVIDIA driver";
    open = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Use NVIDIA's open kernel modules. They need a Turing (GTX 16 or RTX 20
        series) or newer GPU; set false for older cards.
      '';
    };
    driver = lib.mkOption {
      type = lib.types.enum [ "stable" "legacy_580" ];
      default = "stable";
      description = ''
        Driver series. "legacy_580" is the last series that supports Maxwell,
        Pascal and Volta GPUs (GTX 900 and 10 series); it needs open = false.
      '';
    };
    prime = {
      nvidiaBusId = lib.mkOption { type = busId; default = null; example = "PCI:1:0:0"; description = "The NVIDIA GPU's PCI bus ID, for PRIME offload on laptops."; };
      intelBusId = lib.mkOption { type = busId; default = null; example = "PCI:0:2:0"; description = "The built-in Intel GPU's PCI bus ID."; };
      amdgpuBusId = lib.mkOption { type = busId; default = null; example = "PCI:6:0:0"; description = "The built-in AMD GPU's PCI bus ID."; };
    };
    ollamaBackend = lib.mkOption {
      type = lib.types.enum [ "vulkan" "cuda" ];
      default = "vulkan";
      description = "How the local model uses the GPU. \"cuda\" compiles Ollama on the machine.";
    };
  };

  config = lib.mkIf cfg.enable {
    assertions = [{
      assertion = !(cfg.open && cfg.driver == "legacy_580");
      message = "nexusos.nvidia: the legacy_580 driver is for GPUs older than Turing, which need open = false.";
    }];

    # Only NVIDIA's own unfree packages are allowed, not unfree software in general.
    nixpkgs.config.allowUnfreePredicate = pkg:
      let name = lib.getName pkg; in
      builtins.elem name [ "nvidia-x11" "nvidia-kernel-modules" "nvidia-settings" "nvidia-persistenced" ]
      || (cfg.ollamaBackend == "cuda" && (name == "ollama"
            || lib.any (prefix: lib.hasPrefix prefix name) [ "cuda" "libcu" "libnv" "cudnn" ]));

    # Loads the driver; the name is historical and applies to Wayland too.
    services.xserver.videoDrivers = [ "nvidia" ];
    hardware.graphics.enable = true;
    hardware.nvidia = {
      modesetting.enable = true;
      inherit (cfg) open;
      package = config.boot.kernelPackages.nvidiaPackages.${cfg.driver};
      nvidiaSettings = false;
      # Saves video memory across suspend, so the desktop comes back intact.
      powerManagement.enable = true;
      # On laptops, power the NVIDIA GPU down when nothing uses it (Turing and newer).
      powerManagement.finegrained = offload && cfg.open;
      prime = lib.mkIf offload {
        offload = { enable = true; enableOffloadCmd = true; };
        nvidiaBusId = cfg.prime.nvidiaBusId;
        intelBusId = lib.mkIf (cfg.prime.intelBusId != null) cfg.prime.intelBusId;
        amdgpuBusId = lib.mkIf (cfg.prime.amdgpuBusId != null) cfg.prime.amdgpuBusId;
      };
    };

    # When the NVIDIA GPU drives the screens itself, point the desktop's video
    # and GL libraries at it. With offload the desktop stays on the built-in GPU.
    services.nexus-desktop.extraHyprlandConfig = lib.mkIf (!offload) ''
      env = LIBVA_DRIVER_NAME,nvidia
      env = __GLX_VENDOR_LIBRARY_NAME,nvidia
      env = NVD_BACKEND,direct
      cursor {
        no_hardware_cursors = true
      }
    '';

    services.ollama.package = lib.mkIf config.services.ollama.enable
      (if cfg.ollamaBackend == "cuda" then pkgs.ollama-cuda else pkgs.ollama-vulkan);
  };
}
