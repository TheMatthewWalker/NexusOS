# Local image generation for Nexus (ImageStudio.cs): stable-diffusion.cpp
# with Vulkan, and an image model downloaded once the machine is online
# (about 7 GB, too big for the installer image). Until it is there, the
# Images window says why and how far the download has got. The download
# (scripts/images/nexus_image_model.py) needs room for the file plus 2 GiB,
# resumes when interrupted, waits longer after each failure (up to 6 hours, a
# few attempts a day), and keeps the file only if it matches the SHA-256
# pinned here.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.imageModel;
  dir = "/var/lib/nexus-images";
  model = "${dir}/${cfg.file}";
in
{
  options.nexusos.imageModel = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Local image generation, with its model downloaded after installation.";
    };
    url = lib.mkOption {
      type = lib.types.str;
      default = "https://huggingface.co/stabilityai/sdxl-turbo/resolve/main/sd_xl_turbo_1.0_fp16.safetensors";
      description = ''
        The image model. The default, SDXL-Turbo, makes a picture in a few
        steps and fits an 8 GB graphics card; its licence allows personal,
        non-commercial use. Any single-file Stable Diffusion 1.x/2.x/XL model
        works, with steps and guidance set to suit it.
      '';
    };
    sha256 = lib.mkOption {
      type = lib.types.nullOr (lib.types.strMatching "[0-9a-f]{64}");
      # SDXL-Turbo's sd_xl_turbo_1.0_fp16.safetensors (6,938,081,905 bytes):
      # the LFS SHA-256 Hugging Face publishes for it (revision 71153311), as
      # copied by many independent model catalogues. If it were ever wrong,
      # the download is deleted and never used, so it fails safe.
      default = "e869ac7d6942cb327d68d5ed83a40447aadf20e0c3358d98b2cc9e270db0da26";
      description = ''
        SHA-256 of the model file; a download that does not match is deleted.
        Set it with url. null uses the hash the host publishes for the file
        (Hugging Face's X-Linked-Etag) and refuses the download if there is none.
      '';
    };
    size = lib.mkOption {
      type = lib.types.nullOr lib.types.ints.positive;
      default = 6938081905;
      description = "Size of the model file in bytes (null: as the host says), for the free-space check and progress.";
    };
    maxAttemptsPerDay = lib.mkOption {
      type = lib.types.ints.between 1 48;
      default = 4;
      description = "How many download attempts a day at most; between failures the wait doubles from 10 minutes to 6 hours.";
    };
    file = lib.mkOption {
      type = lib.types.str;
      default = "sdxl-turbo.safetensors";
      description = "File name for the model under ${dir}.";
    };
    steps = lib.mkOption { type = lib.types.ints.between 1 60; default = 4; description = "Default steps per image."; };
    guidance = lib.mkOption { type = lib.types.str; default = "1.0"; description = "Default guidance (CFG) scale."; };
    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.stable-diffusion-cpp-vulkan;
      description = "stable-diffusion.cpp, providing sd-server.";
    };
  };

  config = lib.mkIf cfg.enable {
    services.nexus-core.extraEnvironment = {
      NEXUS_SD_SERVER_BIN = "${cfg.package}/bin/sd-server";
      NEXUS_SD_MODEL = model;
      NEXUS_SD_STEPS = toString cfg.steps;
      NEXUS_SD_CFG = cfg.guidance;
    };
    systemd.tmpfiles.rules = [ "d ${dir} 0755 root root -" ];
    # The timer starts the download a few minutes after boot and 15 minutes
    # after each run; the script decides whether to try (backoff, daily limit,
    # free space) and writes what it is doing to ${dir}/download.json.
    systemd.services.nexus-image-model = {
      description = "Download the Nexus image model";
      after = [ "network-online.target" ];
      wants = [ "network-online.target" ];
      path = [ pkgs.curl ];
      serviceConfig = {
        Type = "oneshot";
        TimeoutStartSec = "infinity";
        Nice = 10;
        IOSchedulingClass = "idle";
        # %% so systemd does not read % in a URL as one of its specifiers.
        ExecStart = lib.replaceStrings [ "%" ] [ "%%" ] (lib.escapeShellArgs ([
          "${pkgs.python3.interpreter}" "${../../scripts/images/nexus_image_model.py}"
          "--dir" dir "--file" cfg.file "--url" cfg.url "--max-per-day" (toString cfg.maxAttemptsPerDay)
        ] ++ lib.optionals (cfg.sha256 != null) [ "--sha256" cfg.sha256 ]
          ++ lib.optionals (cfg.size != null) [ "--size" (toString cfg.size) ]));
      };
    };
    systemd.timers.nexus-image-model = {
      description = "Download the Nexus image model when it is missing";
      wantedBy = [ "timers.target" ];
      timerConfig = { OnBootSec = "3min"; OnUnitInactiveSec = "15min"; };
    };
  };
}
