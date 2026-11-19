# NexusOS — AI-native Linux (prototype)

NexusOS is a local-first Linux distribution, built on NixOS, where AI is the
primary *optional* way to use the system. Linux keeps working when the model
or the network is unavailable, and model output can never authorise its own
privileged actions.

This repository contains:

- **Nexus Core**: a per-user .NET 8 daemon and the `nexus` CLI. It provides
  local and cloud model routing, chat history, inference management and
  Guardian plan proposals.
- **Guardian**: a separate root-owned broker that reviews typed NixOS
  changes. Changes are approved by root, attested by a builder, and staged
  for a one-time trial boot that falls back automatically.
- **Desktop (SpaceUI)**: a Hyprland desktop with the Nexus shell
  ([shell/](shell/README.md)), written in Quickshell. It has a deep-space
  wallpaper and a live 3D Nova core that you talk to. Apps come into orbit
  when they fit what you are doing, and open windows orbit a small launcher.
  A system panel shows real audio, network, Bluetooth and battery state.
- **NixOS packaging**: `flake.nix` packages, modules and VM tests.

**Status:** an alpha. The desktop, the installer image (offline, encrypted,
beside Windows) and the AI features are built and covered by CI and VM
tests; what is left is mostly checking it on real hardware, deciding where
releases and updates are published, and a security review. See
[what is left](docs/ROADMAP.md#what-is-left-before-a-10) and the
[evidence](docs/evidence/).

## Quick start (development)

Requires the .NET 8 SDK. Optionally install [Ollama](https://ollama.com) and
run `ollama pull qwen3:8b`.

```bash
dotnet run --project src/NexusOS.Core            # terminal 1: Core
dotnet run --project src/NexusOS.Cli -- health   # terminal 2: CLI
dotnet run --project src/NexusOS.Cli -- ask "Hello Nexus"
```

On Linux, Core listens only on a peer-authenticated Unix socket in its
state directory. On other platforms, or with `NEXUS_API_MODE=tcp`, it uses
`127.0.0.1:5077` with a bearer token. Never expose either on a network.

CLI commands: `health`, `system`, `providers`, `use <local|llama-cpp|colibri|openai-compatible>`,
`ask <prompt>`, `history`, `clear-history`, `status`, `unload`.

## Model providers

| Provider | Setup |
|---|---|
| `local` (Ollama, default) | Ollama on HTTP loopback. `NEXUS_OLLAMA_URL`, `NEXUS_OLLAMA_MODEL`. A missing model gives an `ollama pull` hint |
| `llama-cpp` | Either an existing server (`NEXUS_LLAMA_ENDPOINT`, loopback only), or a **Core-managed** one: set `NEXUS_LLAMA_SERVER_BIN` and `NEXUS_LLAMA_MODEL_PATH` (absolute paths), plus optionally `NEXUS_LLAMA_PORT` and `NEXUS_LLAMA_GPU_LAYERS`. It starts on first use and stops on `nexus unload` |
| `colibri` | The very large model: [colibri](https://github.com/JustVugg/colibri) runs mixture-of-experts models bigger than memory, reading experts from the disk. Core starts `coli serve` on demand from `NEXUS_COLIBRI_BIN`; the model downloads only when the person asks (`/model/colibri/want`, Settings). See [INSTALL](docs/INSTALL.md#the-very-large-model) |
| `openai-compatible` | Off until the operator sets `NEXUS_CLOUD_EGRESS_ALLOWED=true`. The key is read only from the Secret Service keyring: `secret-tool store --label="NexusOS OpenAI" app nexusos provider openai-compatible`. HTTPS on port 443 to `api.openai.com` or hosts listed in `NEXUS_CLOUD_HOSTS`. Every DNS answer is validated, with no redirects or proxies |

Invalid configuration stops Core at startup with a clear message.

## Guardian: changing the system safely

```text
nexus (user) ──propose──▶ model ──typed JSON──▶ Core validates (shared schema)
     └──submit──▶ root broker ── observes running generation ──▶ candidate
root console:  nexus-guardian approve --candidate-id <id> --user <name>
builder (CI):  nexus-guardian-attest sign …   (Ed25519, names the built system)
     └──review──▶ broker verifies attestation + approval ──▶ decision
root:          nexus-guardian-stage --decision <hash>   (one-time trial boot)
next boot:     healthy → confirmed;  failed boot → resets and falls back automatically
```

- Core endpoints: `POST /guardian/propose` (the AI turns a request into a
  typed *preview* and never submits it), `POST /guardian/preview`,
  `POST /guardian/submit`, `GET /guardian/candidates/{id}` and
  `POST /guardian/review`.
- Only options in [schema/guardian-options.json](schema/guardian-options.json)
  can be changed.
- Design: [ADR-002](docs/adr/002-guardian-candidate-model.md). Operations:
  [Guardian deployment](docs/GUARDIAN-DEPLOYMENT.md).

## Install from an image (alpha)

Download the newest release with **Get NexusOS**, which fetches the image's parts, checks them and joins them: on Windows, double-click `Get-NexusOS.cmd` from the [releases page](https://github.com/TheMatthewWalker/NexusOS/releases/latest); on Linux or Mac, `curl -fsSL https://raw.githubusercontent.com/TheMatthewWalker/NexusOS/main/scripts/release/get-nexusos.sh | sh`.

`nix build .#iso` builds a live USB image: a NexusOS desktop that opens the installer when it starts. [docs/INSTALL.md](docs/INSTALL.md) covers writing it to a stick, the install, and what this alpha does not do yet.

## NixOS

To add NexusOS to an existing NixOS configuration instead:

```nix
{
  inputs.nexusos.url = "github:TheMatthewWalker/NexusOS";
  outputs = { nixpkgs, nexusos, ... }: {
    nixosConfigurations.host = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        nexusos.nixosModules.core
        nexusos.nixosModules.guardian
        nexusos.nixosModules.desktop
        {
          services.nexus-core.enable = true;
          services.nexus-desktop.enable = true;   # Hyprland + the Nexus shell (shell/README.md)
          services.nexus-guardian = {
            enable = true;
            allowedUsers = [ "alice" ];
            hostConfig = "path:/etc/nixos#nixosConfigurations.host";
            trustedBuilderKeys = [ "<base64 Ed25519 public key of your builder>" ];
            staging.enable = true;   # GRUB trial boots with automatic fallback
          };
        }
      ];
    };
  };
}
```

## Data and privacy

- **History:** stored in per-user SQLite (`~/.local/state/nexusos/state.sqlite3`,
  mode 0600) as plaintext, not encrypted at rest.
  - Retention: `NEXUS_HISTORY_LIMIT` (default 200, 0 disables saving) and
    `NEXUS_HISTORY_MAX_DAYS` (default 30).
  - `nexus clear-history` deletes it with SQLite `secure_delete`.
  - Legacy JSONL backups need a separate cleanup ([procedure](docs/evidence/M2.md#removing-legacy-plaintext-backups-after-migration)).
- **Logs:** never contain prompts, answers, the API token or provider keys,
  and outbound HTTP header values are redacted.
- **User data:** snapshots are separate from OS rollback, in
  `scripts/backup/nexus_backup.py`.

## Tests

```bash
dotnet test tests/NexusOS.Tests/NexusOS.Tests.csproj     # unit + in-process API + real broker
python3 -m unittest discover -s tests/guardian           # Guardian schema, attestation, broker, staging
python3 -m unittest discover -s tests/backup             # user-data snapshots
python3 -m unittest discover -s tests/bench              # benchmark harness
bash scripts/ci-smoke.sh && bash scripts/ci-unix.sh      # after dotnet publish to out/core, out/cli
bash scripts/ci/cross-user.sh                            # two real Linux accounts (uses sudo)
bash scripts/acceptance/m1-live.sh                       # real Ollama/llama.cpp (see docs/evidence/M1.md)
nix develop .#desktop --command python3 -m unittest tests/shell/test_shell_build.py tests/shell/test_contrast.py
python3 -m unittest tests/cli/test_bridge.py             # CLI streaming + desktop bridge, after publishing to out/
nix flake check                                          # packages + NixOS VM tests, including the Hyprland desktop (KVM recommended)
```

## Documentation

- [Roadmap](docs/ROADMAP.md) and [evidence](docs/evidence/)
- [Architecture](docs/ARCHITECTURE.md), [ADR-002: Guardian](docs/adr/002-guardian-candidate-model.md) and [ADR-003: desktop shell](docs/adr/003-desktop-shell.md)
- [Nexus shell](shell/README.md) and the [SpaceUI design pack](docs/design/spaceui/README.md)
- [Installing NexusOS](docs/INSTALL.md) and [Guardian deployment](docs/GUARDIAN-DEPLOYMENT.md)

Licensed under MIT. Third-party software NexusOS builds or bundles: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
