# NexusOS — AI-native Linux (early prototype)

NexusOS is a local-first, AI-native Linux project. This repository currently starts with **Nexus Core v0.1**: a loopback-only .NET HTTP daemon, a command-line client, local Ollama chat, an OpenAI-compatible provider and read-only system facts.

**Status:** starter implementation; compile and integration tests must be run on a machine with the .NET SDK. Not yet an OS distribution. **Do not expose port 5077 on a network.** No authentication, durable settings, multi-user isolation or Guardian executor exists yet.

## Run

Requires .NET 8+ SDK and optionally Ollama with `llama3.2:3b` pulled.

Terminal 1:

```bash
dotnet run --project src/NexusOS.Core
```

Terminal 2:

```bash
dotnet run --project src/NexusOS.Cli -- health
dotnet run --project src/NexusOS.Cli -- system
dotnet run --project src/NexusOS.Cli -- providers
dotnet run --project src/NexusOS.Cli -- ask "Hello Nexus"
```

For local AI, install and start Ollama, then run `ollama pull llama3.2:3b`. Override the local model with `NEXUS_OLLAMA_MODEL` and base URL with `NEXUS_OLLAMA_URL` (only trusted endpoints).

For an OpenAI-compatible provider, install `libsecret-tools` and ensure a user Secret Service keyring is unlocked. Enter your API key with `secret-tool store --label="NexusOS OpenAI" app nexusos provider openai-compatible` (type or paste the key on stdin, then finish with EOF). Set `NEXUS_OPENAI_ENDPOINT` and `NEXUS_OPENAI_MODEL` if required, restart Core, then:

```bash
dotnet run --project src/NexusOS.Cli -- use openai-compatible
dotnet run --project src/NexusOS.Cli -- ask "Explain Linux namespaces"
```

**Security notes:** API credentials are read from the Linux Secret Service keyring; never place credentials in configuration or CI logs. Do not commit secrets, do not bind the daemon publicly, and do not assume OpenAI-compatible means every cloud provider works without adaptation.

See [the roadmap](docs/ROADMAP.md) and [architecture](docs/ARCHITECTURE.md).


## M2 experimental persistence

Provider selection and successful chat requests now persist under `$XDG_STATE_HOME/nexusos` (default `~/.local/state/nexusos`; override with `NEXUS_STATE_DIR`). Use `nexus history` for the 50 most recent conversations. The history file is JSONL; it is **plaintext**, contains both prompts and responses, and is intended for local development only. Don't send passwords, API tokens or confidential documents in chat while persistence is enabled. There is not yet an authenticated IPC layer or encrypted credential vault. File permissions are restricted on Unix, but the HTTP service is still unauthenticated on loopback. These limitations block marking M2 complete.


## M2 local API authentication (development)

Nexus Core now creates a random per-user bearer token in `$NEXUS_STATE_DIR/api-token` (or the XDG state directory) and requires it on **every** endpoint, including health checks. The CLI loads the token from the same user's state directory automatically; start Core before running the CLI. Direct API clients must send `Authorization: Bearer <token>`. The file must not be committed, shared, or logged. For development, changing/removing the token while Core is stopped regenerates authentication at startup; existing clients must reconnect.

**Important limitations:** This is initial loopback authentication, not a completed multi-user security boundary. Other processes running as the same Linux user can still read the token. Core is still bound to 127.0.0.1:5077, and chat transcripts remain plaintext. The eventual NexusOS daemon will use authenticated Unix-domain sockets with peer identity checks and an OS secrets service. Avoid privileged capabilities until those are in place.


## History retention controls (M2)

`NEXUS_HISTORY_LIMIT` sets the maximum number of saved successful conversations (default 200, accepted range 0–1000, with 0 disabling future saves). `nexus clear-history` removes currently saved conversation entries. History remains unencrypted plaintext and must not contain secrets. The clear operation is a normal file replacement, not secure erasure from snapshots, backups or storage media. HTTP requests are limited to 64 KiB. Future work includes a keyring-backed secret store, expiry retention and peer-authenticated Unix sockets.


## M2 provider endpoint restrictions

Cloud API calls default to `https://api.openai.com/v1`. Additional trusted HTTPS hosts must be explicitly listed in `NEXUS_CLOUD_HOSTS` (comma-separated exact hostnames); only port 443 is supported. The local Ollama URL must be HTTP loopback, and `/chat` has a basic 20-request/minute single-user rate limit. This is a first network policy, **not** complete protection against DNS rebinding, redirects, proxy configuration, model response abuse, or untrusted tools. Avoid adding privileged tools until those boundaries are hardened.


## Guardian preview prototype (M3)

`POST /guardian/preview` accepts a description and an array of `{option, enabled}` entries for four explicitly allowlisted NixOS boolean options. It returns a deterministic plan ID, textual Nix preview and risk classification. **This endpoint never edits files, executes Nix commands, requests root privileges, or applies changes.** It is not a complete Guardian workflow: Nix evaluation, diff against the active system, user confirmation, tamper-resistant audit trails, VM tests and rollback are future work.


## M5 inference lifecycle prototype

Authenticated `GET /inference/status` checks whether Ollama responds, and `POST /inference/unload` sends an Ollama `keep_alive=0` request for the configured local model. This is a request to unload, not a guarantee of reclaimed VRAM. The budget planner supports tests for reserving GPU headroom. There is not yet GPU telemetry, a llama.cpp adapter, preemption, active KV-cache control, automatic idle eviction, nor measured performance on real hardware.


## Guardian acknowledgement audit (M3 prototype)

`POST /guardian/acknowledge` requires the exact reviewed plan ID and the literal acknowledgement `I APPROVE`. It writes a hash-chained entry in `guardian-audit.jsonl` and returns `executed:false`. No system change is made. This chain detects accidental edits, but it is **not immutable or tamper-proof** against a process with write access to the user's files; the production executor and audit must live in a separately privileged service. No LLM may directly execute system commands.


## Linux Secret Service integration (M2)

For cloud API requests, Nexus Core reads the `openai-compatible` credential from the logged-in user's Secret Service through `secret-tool`. It does not accept `NEXUS_OPENAI_API_KEY` as an alternate plaintext environment source. A headless daemon without an unlocked keyring will fail cloud requests cleanly; local Ollama remains usable. This is Linux-specific until other OS credential providers are implemented. The code does not automatically install, unlock or persist keys in files.


## Linux resource telemetry (M5)

`GET /inference/telemetry` reports system RAM using `/proc/meminfo` and optional first-GPU VRAM statistics using `nvidia-smi` when available. Missing GPU data is reported as unavailable instead of fabricated. AMD/Intel support, GPU process accounting, telemetry sampling, automatic eviction and VRAM reclamation validation remain outstanding. Do not infer that a model fits in memory from total VRAM alone.
