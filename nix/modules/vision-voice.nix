# "Describe this" and voice control for Nexus (Vision.cs, Voice.cs), all on
# this computer:
#  - a small vision model in the local Ollama, downloaded once the machine is
#    online, as the chat model is (nexus-model-pull); hardware.nix picks its
#    size for the computer;
#  - whisper.cpp for push-to-talk (speech to text) and Piper for spoken
#    replies, with an English speech model and one British English voice
#    downloaded once online into /var/lib/nexus-voice.
# The programs are the same on every machine (they are in the installed
# system's closure); only the downloaded models differ, and those live
# outside the Nix store. Nothing listens until the person holds Super+V or
# presses the microphone button in Nexus, and the desktop shows when the
# microphone is open.
{ config, lib, pkgs, ... }:
let
  vision = config.nexusos.vision;
  voice = config.nexusos.voice;
  dir = "/var/lib/nexus-voice";
  whisperModel = "${dir}/${voice.speechModel.file}";
  piperVoice = "${dir}/${voice.voice.file}";
in
{
  options.nexusos.vision = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = ''
        Describe pictures and the screen with a local vision model (Settings,
        Accessibility; Explain on a picture; Nexus's describe_screen card).
        Pictures are taken only when the person asks and never leave the computer.
      '';
    };
    model = lib.mkOption {
      type = lib.types.str;
      default = "qwen2.5vl:3b";
      example = "moondream";
      description = ''
        The Ollama vision model. nexusos.hardware.visionModel chooses one for
        the computer: qwen2.5vl:7b with a graphics card for AI, qwen2.5vl:3b
        (about 3 GB, runs on the processor of a 16 GB laptop) otherwise, and
        moondream on small machines.
      '';
    };
  };

  options.nexusos.voice = {
    enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Push-to-talk speech to text (whisper.cpp) and optional spoken replies (Piper), on this computer.";
    };
    speechModel = {
      url = lib.mkOption {
        type = lib.types.str;
        default = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
        description = "The whisper.cpp model (ggml). base.en: 142 MB, English, quick on a laptop processor.";
      };
      file = lib.mkOption { type = lib.types.str; default = "ggml-base.en.bin"; description = "File name under ${dir}."; };
      sha1 = lib.mkOption {
        type = lib.types.nullOr lib.types.str;
        # From whisper.cpp's models/README.md at v1.8.4 (the version in nixpkgs).
        default = "137c40403d78fd54d454da0f9bd998f78703390c";
        description = ''
          SHA-1 of the model as whisper.cpp publishes it; a download that does
          not match is deleted. null keeps the file as downloaded (still checked
          against the host's published SHA-256 when it gives one).
        '';
      };
    };
    voice = {
      url = lib.mkOption {
        type = lib.types.str;
        default = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_GB/alba/medium/en_GB-alba-medium.onnx";
        description = "The Piper voice (.onnx); its .onnx.json is fetched from the same address.";
      };
      file = lib.mkOption { type = lib.types.str; default = "en_GB-alba-medium.onnx"; description = "File name under ${dir}."; };
      sha256 = lib.mkOption {
        type = lib.types.nullOr lib.types.str;
        # Not pinned: the checksum could not be checked when this was written.
        # The download is checked against the SHA-256 Hugging Face publishes
        # for the file (X-Linked-Etag), as the image model's is.
        default = null;
        description = "SHA-256 of the voice file; null checks it against the host's published SHA-256 only.";
      };
    };
    whisperPackage = lib.mkOption { type = lib.types.package; default = pkgs.whisper-cpp; description = "whisper.cpp, providing whisper-cli."; };
    piperPackage = lib.mkOption {
      type = lib.types.package;
      # Speaking only: without the training extras (PyTorch and the rest), which are gigabytes.
      default = pkgs.piper-tts.override { withTrain = false; withHTTP = false; withAlignment = false; };
      description = "Piper, providing piper.";
    };
  };

  config = lib.mkMerge [
    (lib.mkIf vision.enable {
      services.nexus-core.extraEnvironment.NEXUS_VISION_MODEL = vision.model;
      # The same pattern as nexus-model-pull, after it, so the chat model comes first.
      systemd.services.nexus-vision-model-pull = lib.mkIf config.services.ollama.enable {
        description = "Download the Nexus vision model";
        wantedBy = [ "multi-user.target" ];
        after = [ "ollama.service" "network-online.target" "nexus-model-pull.service" ];
        wants = [ "network-online.target" ];
        requires = [ "ollama.service" ];
        environment.OLLAMA_HOST = "127.0.0.1:11434";
        serviceConfig = { Type = "oneshot"; Restart = "on-failure"; RestartSec = 300; Nice = 10; };
        script = ''
          model=${lib.escapeShellArg vision.model}
          if ${config.services.ollama.package}/bin/ollama show "$model" > /dev/null 2>&1; then
            echo "$model is already present"
          else
            ${config.services.ollama.package}/bin/ollama pull "$model"
          fi
        '';
      };
    })
    (lib.mkIf voice.enable {
      services.nexus-core.extraEnvironment = {
        NEXUS_WHISPER_BIN = "${voice.whisperPackage}/bin/whisper-cli";
        NEXUS_WHISPER_MODEL = whisperModel;
        NEXUS_PIPER_BIN = "${voice.piperPackage}/bin/piper";
        NEXUS_PIPER_VOICE = piperVoice;
      };
      systemd.tmpfiles.rules = [ "d ${dir} 0755 root root -" ];
      systemd.services.nexus-voice-models = {
        description = "Download the Nexus speech model and voice";
        wantedBy = [ "multi-user.target" ];
        after = [ "network-online.target" ];
        wants = [ "network-online.target" ];
        path = [ pkgs.curl pkgs.coreutils pkgs.gawk ];
        serviceConfig = { Type = "oneshot"; Restart = "on-failure"; RestartSec = 300; Nice = 10; IOSchedulingClass = "idle"; };
        script = ''
          set -eu
          # fetch <url> <file> <sha1 or empty> <sha256 or empty>
          fetch() {
            [ -f "$2" ] && { echo "$2 is present."; return 0; }
            # Hugging Face publishes each large file's SHA-256 as X-Linked-Etag.
            published=$(curl -sfIL "$1" | tr -d '\r' | awk -F'"' 'tolower($0) ~ /^x-linked-etag:/ {print $2}' | tail -1)
            echo "Downloading $1 (resumes if interrupted)…"
            curl -fL --retry 5 -C - -o "$2.part" "$1"
            check() {
              actual=$($1 "$2.part" | cut -d' ' -f1)
              if [ "$actual" != "$3" ]; then
                echo "Checksum mismatch for $2: got $actual, expected $3. Deleting the download." >&2
                rm -f "$2.part"; exit 1
              fi
            }
            [ -n "$3" ] && check sha1sum "$2" "$3"
            [ -n "$4" ] && check sha256sum "$2" "$4"
            [ -n "$published" ] && check sha256sum "$2" "$published"
            chmod 0644 "$2.part"
            mv "$2.part" "$2"
          }
          fetch ${lib.escapeShellArgs [ voice.speechModel.url whisperModel (toString voice.speechModel.sha1) "" ]}
          fetch ${lib.escapeShellArgs [ voice.voice.url piperVoice "" (toString voice.voice.sha256) ]}
          # The voice's settings: small, not published with a checksum.
          fetch ${lib.escapeShellArgs [ "${voice.voice.url}.json" "${piperVoice}.json" "" "" ]}
          echo "Speech to text and spoken replies are ready."
        '';
      };
    })
  ];
}
