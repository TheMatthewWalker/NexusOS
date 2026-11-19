# What this computer is, as nexus-install found it, and what follows from
# that: drivers for the graphics, power care for laptops, and a local model
# the computer can run at a usable speed. Nobody picks drivers by hand: the
# installer writes nexusos.hardware.* to local.nix and this module does the rest.
#
# Every built-in GPU (Intel, AMD) is driven by the kernel and Mesa, which every
# machine carries; only video decoding needs the vendor's VA-API driver, so
# both are always installed and each GPU loads its own. NVIDIA cards need the
# proprietary driver: nix/modules/nvidia.nix, turned on by the installer.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.hardware;
  # A GPU that can hold a model in its own memory.
  aiGpu = cfg.gpu == "nvidia" || config.nexusos.nvidia.enable || (cfg.gpu == "amd" && cfg.gpuMemoryGiB >= 6);
in
{
  options.nexusos.hardware = {
    cpu = lib.mkOption {
      type = lib.types.enum [ "intel" "amd" "other" ];
      default = "other";
      description = "The processor's maker.";
    };
    gpu = lib.mkOption {
      type = lib.types.enum [ "intel" "amd" "nvidia" "none" ];
      default = "none";
      description = ''
        The strongest graphics processor. "intel" and "amd" with little
        memory of their own are built in and share the computer's memory.
      '';
    };
    gpuMemoryGiB = lib.mkOption {
      type = lib.types.ints.unsigned;
      default = 0;
      description = "The GPU's own memory, where the installer could read it (AMD); 0 if unknown or shared.";
    };
    memoryGiB = lib.mkOption {
      type = lib.types.ints.unsigned;
      default = 16;
      description = "The computer's memory (RAM).";
    };
    laptop = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Runs on a battery: turns on thermal and power care.";
    };
    aiModel = lib.mkOption {
      type = lib.types.str;
      readOnly = true;
      description = "The local model this computer runs well (services.nexus-core.ollamaModel follows it).";
      default =
        if aiGpu then "qwen3:8b"
        # On the processor alone, the 8B model answers at a few words a
        # second; the 4B one is about twice as fast and still follows Nexus's
        # guide. Very small machines get the smallest one.
        else if cfg.memoryGiB >= 8 then "qwen3:4b"
        else "qwen3:1.7b";
    };
    visionModel = lib.mkOption {
      type = lib.types.str;
      readOnly = true;
      description = "The local vision model for Describe this (nexusos.vision.model follows it).";
      default =
        if aiGpu then "qwen2.5vl:7b"
        # About 3 GB; on the processor of a 16 GB laptop it describes a window in well under a minute.
        else if cfg.memoryGiB >= 12 then "qwen2.5vl:3b"
        else "moondream";
    };
  };

  config = {
    hardware.graphics.enable = true;
    hardware.graphics.extraPackages = lib.optionals pkgs.stdenv.hostPlatform.isx86_64 [
      pkgs.intel-media-driver    # Intel Broadwell (2014) and newer
      pkgs.intel-vaapi-driver    # older Intel
    ];

    # Intel's thermal daemon keeps laptops from throttling hard; it only knows Intel processors.
    services.thermald.enable = lib.mkDefault (cfg.laptop && cfg.cpu == "intel");

    services.nexus-core.ollamaModel = lib.mkDefault cfg.aiModel;

    # An AMD card with its own memory runs the model through Vulkan; built-in
    # GPUs share the computer's memory and are no faster than the processor.
    services.ollama.package = lib.mkIf (config.services.ollama.enable && cfg.gpu == "amd" && aiGpu)
      (lib.mkDefault pkgs.ollama-vulkan);

    # Pictures take about 8 GB of memory and a 7 GB download; without a GPU
    # for AI they need a computer with memory to spare.
    nexusos.imageModel.enable = lib.mkDefault (aiGpu || cfg.memoryGiB >= 16);

    # The large mixture-of-experts model (large-model.nix) needs a 16 GB
    # computer (which reports 15 GiB). With a graphics card for AI most of it
    # sits on the card and it becomes the model Nexus answers with; on the
    # processor alone it takes most of the memory, so it is there to switch to.
    nexusos.largeModel.enable = lib.mkDefault (cfg.memoryGiB >= 15);
    nexusos.largeModel.preferred = lib.mkDefault aiGpu;

    # The very large model (colibri.nix) reads most of itself from the disk, so
    # it needs less memory than its size: offered from 8 GB, downloaded only
    # when the person turns it on. Its engine is built for x86-64.
    nexusos.colibri.enable = lib.mkDefault (pkgs.stdenv.hostPlatform.isx86_64 && cfg.memoryGiB >= 8);
    # Qwen3-Coder needs 8 GB at least; Qwen3.6 needs 10.
    nexusos.colibri.model = lib.mkDefault (if cfg.memoryGiB >= 10 then "qwen36-35b" else "qwen3-coder-30b");

    # Describe this: the vision model's size follows the computer; with less
    # than 8 GB of memory it would crowd out the chat model, so it is off.
    nexusos.vision.model = lib.mkDefault cfg.visionModel;
    nexusos.vision.enable = lib.mkDefault (aiGpu || cfg.memoryGiB >= 8);
  };
}
