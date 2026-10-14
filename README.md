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
