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


## Automatic idle model unloading (experimental M5)

Set `NEXUS_MODEL_IDLE_SECONDS` to a value of at least 30 to enable a periodic, best-effort Ollama model-unload request after inactivity. The service is disabled by default, checks every ten seconds, and attempts one unload per interval of request activity. This is not GPU preemption and makes no guarantee of actual memory reclamation. Background tasks other than `/chat` aren't yet tracked, and failures do not interrupt the OS.


## Local llama.cpp server (M5)

Nexus Core can now route requests to a separate llama.cpp HTTP server using `nexus use llama-cpp`. Configure `NEXUS_LLAMA_ENDPOINT` (defaults to HTTP loopback port 8080) and `NEXUS_LLAMA_MODEL` (defaults to `default`). This requires an independently running llama.cpp server with an OpenAI-compatible `/v1/chat/completions` endpoint. The service does not yet launch the model, pick quantisation, allocate CPU/GPU layers, or automatically offload model weights. CI tests the protocol through a mock only.


## Local inference benchmark (M5 hardware acceptance)

On a workstation with Ollama and a model loaded, run `python3 scripts/bench-ollama.py --model llama3.2:3b --runs 3`. It records wall-clock latency and Ollama-reported output tokens per second when available. It limits endpoints to HTTP loopback and response bodies to 4 MiB. To study cold starts and offloading, compare runs before and after explicitly unloading the model, while separately capturing VRAM telemetry. This script does not prove VRAM reclamation or SSD-based model paging and does not itself run on GPU hardware in CI.


## Unix-domain API transport (M2)

On Linux, set `NEXUS_API_MODE=unix` for both Core and CLI to communicate over `$NEXUS_STATE_DIR/nexus.sock` rather than TCP. The socket lives within a per-user mode-0700 state directory and requests still require the local bearer token. Core generates the token before opening the listener. A stale socket left after an unclean shutdown must be removed manually after verifying the old process is stopped. TCP is still the development default; privileged Guardian operations require a separately authenticated executor.


## Inference concurrency limits (M2)

Nexus Core now rejects a chat request with HTTP 429 when all inference slots are busy. The default is one in-flight inference request; `NEXUS_MAX_CONCURRENT_INFERENCE` allows 1–8 slots (values outside the range are clamped). Slots are released in a `finally`-safe lease even on backend failure or cancellation. This provides backpressure and helps avoid accidental simultaneous model loads. It does not yet implement model-specific VRAM admission control or queue prioritisation.


## Inference token budgets (M5)

`NEXUS_CONTEXT_TOKENS` (default 4096, supported 512–131072) and `NEXUS_MAX_OUTPUT_TOKENS` (default 512, must be less than context) provide a bounded budget. Ollama receives `num_ctx`/`num_predict`; llama.cpp and the cloud adapter receive `max_tokens`. Invalid configurations fail rather than silently allowing unbounded generation. **This is an advertised token budget, not a tokeniser or admission controller:** provider/model support varies and a prompt may still exceed its available context. KV-cache memory and actual truncation are not yet measured or guaranteed.


## Cloud egress (M2)

Sending prompts to the OpenAI-compatible provider is disabled by default. The Core operator must explicitly set `NEXUS_CLOUD_EGRESS_ALLOWED=true` to permit external transmission. Provider selection alone is insufficient. This is a process-level operator consent guard, not per-message approval or a network firewall.
