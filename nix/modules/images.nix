# Local image generation for Nexus (ImageStudio.cs): stable-diffusion.cpp
# with Vulkan, and an image model downloaded once the machine is online
# (about 7 GB, too big for the installer image). Until it is there, the
# Images window says so. The download is checked against the SHA-256 the
# model host publishes for the file.
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
    systemd.services.nexus-image-model = {
      description = "Download the Nexus image model";
      wantedBy = [ "multi-user.target" ];
      after = [ "network-online.target" ];
      wants = [ "network-online.target" ];
      path = [ pkgs.curl pkgs.coreutils pkgs.gnugrep pkgs.gawk ];
      serviceConfig = { Type = "oneshot"; Restart = "on-failure"; RestartSec = 300; Nice = 10; IOSchedulingClass = "idle"; };
      script = ''
        set -eu
        [ -f ${lib.escapeShellArg model} ] && { echo "The image model is present."; exit 0; }
        url=${lib.escapeShellArg cfg.url}
        # Hugging Face publishes each large file's SHA-256 as X-Linked-Etag.
        expected=$(curl -sfIL "$url" | tr -d '\r' | awk -F'"' 'tolower($0) ~ /^x-linked-etag:/ {print $2}' | tail -1)
        echo "Downloading the image model (resumes if interrupted)…"
        curl -fL --retry 5 -C - -o ${lib.escapeShellArg "${model}.part"} "$url"
        if [ -n "$expected" ]; then
          actual=$(sha256sum ${lib.escapeShellArg "${model}.part"} | cut -d' ' -f1)
          if [ "$actual" != "$expected" ]; then
            echo "Checksum mismatch: got $actual, expected $expected. Deleting the download." >&2
            rm -f ${lib.escapeShellArg "${model}.part"}
            exit 1
          fi
        else
          echo "No published checksum for this file; keeping it as downloaded." >&2
        fi
        chmod 0644 ${lib.escapeShellArg "${model}.part"}
        mv ${lib.escapeShellArg "${model}.part"} ${lib.escapeShellArg model}
        echo "The image model is ready."
      '';
    };
  };
}
