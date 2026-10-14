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

For an OpenAI-compatible provider, set `NEXUS_OPENAI_API_KEY`, optionally `NEXUS_OPENAI_ENDPOINT` and `NEXUS_OPENAI_MODEL`, restart Core, then:

```bash
dotnet run --project src/NexusOS.Cli -- use openai-compatible
dotnet run --project src/NexusOS.Cli -- ask "Explain Linux namespaces"
```

**Security notes:** API credentials via environment variables are a temporary developer-only measure. Do not commit secrets, do not bind the daemon publicly, and do not assume OpenAI-compatible means every cloud provider works without adaptation.

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
