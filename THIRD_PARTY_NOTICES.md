# Third-party notices

NexusOS is built from many open-source projects, nearly all of them taken
from nixpkgs with their own licences (see each package's `meta.license`).
This file lists the software NexusOS builds or bundles from outside nixpkgs,
and the model licences it relies on.

## colibri

- Source: https://github.com/JustVugg/colibri, commit `0a5504a1dc64f86fcd0fe7db298b0448c730e7e6` (version 2.1.0)
- Copyright 2026 Vincenzo Fornaro
- Licence: Apache License, Version 2.0
- Used for: the very large model (`nix/packages/colibri.nix`, `nix/modules/colibri.nix`)
- Changes: none to its source. NexusOS builds three of its engines (`qwen36`,
  `colibri`, `deepseek_v4`) with Vulkan, installs `coli` with the Python files
  its server and downloader need, and leaves out the converter tools. Its
  `LICENSE`, `NOTICE` and `THIRD_PARTY_NOTICES.md` (MIT and Apache-2.0
  components it bundles) are installed with it in `share/doc/colibri`, and on
  an installed system in `/etc/nexusos/licences/colibri`.
- NexusOS's download helper (`scripts/colibri/nexus_colibri_model.py`) calls
  colibri's own `setup_download` and `setup_catalog` modules.

The Apache License 2.0 lets NexusOS use, modify and redistribute colibri,
including in a commercial product, as long as the licence and NOTICE go with
it and changes are stated (above). It gives no trademark rights: NexusOS calls
it "colibri" only to say what it is.

### Models offered through colibri

Downloaded only when the person asks, from Hugging Face. Only models with
permissive licences are offered:

| Model | Licence |
|---|---|
| Qwen3.6-35B-A3B, Qwen3-Coder-30B-A3B | Apache-2.0 |
| DeepSeek V4 Flash | MIT |
| GLM-5.2 | MIT |

colibri's catalogue also lists Qwen-Image-2.1 (Qwen Research License,
non-commercial only) and Muse Spark Open (Meta's licence agreement). NexusOS
does not offer them.

## The large model

`DavidAU/OpenAi-GPT-oss-20b-HERETIC-uncensored-NEO-Imatrix-gguf` is a
quantisation of OpenAI's gpt-oss-20b (Apache-2.0), downloaded when the
computer has 16 GB of memory. Check the repository's own model card for any
further terms before shipping it in a product.
