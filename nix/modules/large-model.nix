# The large local model for Nexus (LargeModel.cs): a mixture-of-experts
# model, much bigger than the standard one, on a computer that could not
# hold a dense model of its size. Each word such a model writes uses only a
# few of its experts, so llama.cpp keeps the experts in ordinary memory and
# puts the part every word needs on the graphics card (--n-cpu-moe); Core
# works out the split from the graphics memory that is there, and without a
# card the model runs from memory on the processor.
#
# The default, gpt-oss-20b at 4 bits, is about 12 GB with 3.6 billion of its
# 21 billion parameters active per word: it fits a 16 GB computer, where
# nothing bigger does. It is downloaded once the machine is online (too big
# for the installer image) by the same careful script as the image model:
# room for the file plus 2 GiB, resume, back-off, and kept only if it matches
# its SHA-256. Until it is there, and whenever memory is short, the standard
# model answers.
{ config, lib, pkgs, ... }:
let
  cfg = config.nexusos.largeModel;
  dir = "/var/lib/nexus-large-model";
  model = "${dir}/${cfg.file}";
in
{
  options.nexusos.largeModel = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Download and offer the large mixture-of-experts model (nix/modules/hardware.nix turns it on with 16 GB of memory).";
    };
    preferred = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = ''
        Answer with the large model once it is downloaded, unless the person
        chose otherwise in Settings. Off: it is there to switch to.
      '';
    };
    name = lib.mkOption { type = lib.types.str; default = "gpt-oss-20b"; description = "What Settings and Nexus call the model."; };
    url = lib.mkOption {
      type = lib.types.str;
      default = "https://huggingface.co/unsloth/gpt-oss-20b-GGUF/resolve/main/gpt-oss-20b-Q4_K_M.gguf";
      description = "Where the GGUF model file is downloaded from.";
    };
    sha256 = lib.mkOption {
      type = lib.types.nullOr (lib.types.strMatching "[0-9a-f]{64}");
      default = null;
      description = ''
        SHA-256 of the model file; a download that does not match is deleted.
        null uses the hash the host publishes for the file (Hugging Face's
        X-Linked-Etag) and refuses the download if there is none.
      '';
    };
    size = lib.mkOption {
      type = lib.types.ints.positive;
      default = 11600000000;
      description = "Size of the model file in bytes, about: for the free-space check, progress, and the memory split.";
    };
    layers = lib.mkOption {
      type = lib.types.ints.between 1 512;
      default = 24;
      description = "The model's layers (blocks), for splitting its experts between graphics memory and ordinary memory.";
    };
    reasoningEffort = lib.mkOption {
      type = lib.types.nullOr (lib.types.enum [ "low" "medium" "high" ]);
      default = "low";
      description = "How long a model that thinks before answering (gpt-oss) may think; low keeps answers quick on a laptop. null: the model's default.";
    };
    file = lib.mkOption { type = lib.types.str; default = "large-model.gguf"; description = "File name for the model under ${dir}."; };
    package = lib.mkOption {
      type = lib.types.package;
      default = pkgs.llama-cpp-vulkan;
      description = "llama.cpp, providing llama-server. The Vulkan build uses any graphics card and runs on the processor without one.";
    };
  };

  config = lib.mkIf cfg.enable {
    services.nexus-core.extraEnvironment = {
      NEXUS_LLAMA_SERVER_BIN = "${cfg.package}/bin/llama-server";
      NEXUS_LLAMA_MODEL_PATH = model;
      # Its own port: 8080 is what people run their own things on.
      NEXUS_LLAMA_PORT = "18435";
      # Reading 12 GB from a laptop's disk takes a while the first time.
      NEXUS_LLAMA_START_TIMEOUT_SECONDS = "420";
      NEXUS_LARGE_MODEL_NAME = cfg.name;
      NEXUS_LARGE_MODEL_BYTES = toString cfg.size;
      NEXUS_LARGE_MODEL_LAYERS = toString cfg.layers;
      NEXUS_LARGE_MODEL_PREFERRED = lib.boolToString cfg.preferred;
      NEXUS_LARGE_MODEL_STATUS = "${dir}/download.json";
    } // lib.optionalAttrs (cfg.reasoningEffort != null) { NEXUS_LLAMA_REASONING_EFFORT = cfg.reasoningEffort; };
    systemd.tmpfiles.rules = [ "d ${dir} 0755 root root -" ];
    systemd.services.nexus-large-model = {
      description = "Download the large Nexus model";
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
          "--dir" dir "--file" cfg.file "--url" cfg.url "--size" (toString cfg.size) "--what" "large model"
        ] ++ lib.optionals (cfg.sha256 != null) [ "--sha256" cfg.sha256 ]));
      };
    };
    systemd.timers.nexus-large-model = {
      description = "Download the large Nexus model when it is missing";
      wantedBy = [ "timers.target" ];
      # After the image model's first try, so two big downloads do not start together.
      timerConfig = { OnBootSec = "6min"; OnUnitInactiveSec = "15min"; };
    };
  };
}
