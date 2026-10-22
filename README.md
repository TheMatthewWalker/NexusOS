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
- **NixOS packaging**: `flake.nix` packages, modules and VM tests.

**Status:** M0 is done, M1–M3 are done pending review, M4 is VM-proven
with a hardware drill outstanding, and M5's tooling is done with GPU runs
outstanding. There is no desktop UI or installer image yet. See the
[roadmap](docs/ROADMAP.md) and the [evidence](docs/evidence/).

## Quick start (development)

Requires the .NET 8 SDK. Optionally install [Ollama](https://ollama.com) and
run `ollama pull llama3.2:3b`.

```bash
dotnet run --project src/NexusOS.Core            # terminal 1: Core
dotnet run --project src/NexusOS.Cli -- health   # terminal 2: CLI
dotnet run --project src/NexusOS.Cli -- ask "Hello Nexus"
```

On Linux, Core listens only on a peer-authenticated Unix socket in its
state directory. On other platforms, or with `NEXUS_API_MODE=tcp`, it uses
`127.0.0.1:5077` with a bearer token. Never expose either on a network.

CLI commands: `health`, `system`, `providers`, `use <local|llama-cpp|openai-compatible>`,
`ask <prompt>`, `history`, `clear-history`, `status`, `unload`.

## Model providers

| Provider | Setup |
|---|---|
| `local` (Ollama, default) | Ollama on HTTP loopback. `NEXUS_OLLAMA_URL`, `NEXUS_OLLAMA_MODEL`. A missing model gives an `ollama pull` hint |
| `llama-cpp` | Either an existing server (`NEXUS_LLAMA_ENDPOINT`, loopback only), or a **Core-managed** one: set `NEXUS_LLAMA_SERVER_BIN` and `NEXUS_LLAMA_MODEL_PATH` (absolute paths), plus optionally `NEXUS_LLAMA_PORT` and `NEXUS_LLAMA_GPU_LAYERS`. It starts on first use and stops on `nexus unload` |
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

## NixOS

```nix
{
  inputs.nexusos.url = "github:TheMatthewWalker/NexusOS";
  outputs = { nixpkgs, nexusos, ... }: {
    nixosConfigurations.host = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        nexusos.nixosModules.core
        nexusos.nixosModules.guardian
        {
          services.nexus-core.enable = true;
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
nix flake check                                          # packages + NixOS VM tests (KVM recommended)
```

## Documentation

- [Roadmap](docs/ROADMAP.md) and [evidence](docs/evidence/)
- [Architecture](docs/ARCHITECTURE.md) and [ADR-002: Guardian](docs/adr/002-guardian-candidate-model.md)
- [Guardian deployment](docs/GUARDIAN-DEPLOYMENT.md)

Licensed under MIT.
