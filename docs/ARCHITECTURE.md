# NexusOS Architecture Decision Record 001

## Goals

AI is the primary *optional* interaction channel. Linux stays usable when
inference or the internet is unavailable. Model output cannot authorise its
own privileged actions.

## Components

| Component | Status | Role |
|---|---|---|
| `Nexus.Core` (`src/NexusOS.Core`) | Implemented | Unprivileged per-user daemon: provider registry (`IModelProvider`), history, token budgets, telemetry, Guardian plan proposal and broker client |
| `nexus` CLI (`src/NexusOS.Cli`) | Implemented | User interface to Core over its socket |
| `Nexus.Inference` | Partly implemented (in Core) | Ollama and llama.cpp adapters, managed `llama-server` lifecycle, unload and idle eviction, RAM/VRAM telemetry, benchmark harness. Foreground preemption is open |
| `Nexus.Guardian` (`scripts/guardian`, `nix/modules/guardian.nix`) | Implemented | Root broker, root approval console, Ed25519 attestation tool, staging executor, boot check |
| `Nexus.Desktop` | Not started (M6) | Hyprland/Quickshell command palette, provider widget, change-proposal view |
| `Nexus.Search` (in Core, `Search*.cs`) | First version (M8) | Local index over folders the person chooses: SQLite FTS5 plus local Ollama embeddings; see [Local search index](#local-search-index-m8-feature-8) |
| `Nexus.Skills` | Not started (M9) | Capability-scoped, signed tools |

## Trust boundaries

1. **LLM text is untrusted data.** It can only become a typed plan preview,
   validated against one shared schema, and never directly a command,
   approval or submission.
2. **Core is unprivileged and per user.** Its Unix socket is in a 0700
   directory. Callers are identified by kernel `SO_PEERCRED` and must also
   hold a bearer token. TCP is a loopback-only development opt-in.
3. **Guardian decides as root, in a sandbox.** It has no capabilities and
   no network. It re-observes the running generation itself, accepts only
   root-console approvals and verifies Ed25519 builder attestations with
   public keys only (ADR-002).
4. **Only the staging executor and boot check touch the system profile and
   bootloader**, and only via `switch-to-configuration boot` plus a GRUB
   one-time entry. The running system is never switched.
5. **Recovery needs no LLM, network or user input.** A failed trial boot
   resets (`panic=`) and GRUB returns to the saved baseline.
6. **Cloud egress requires explicit operator consent.** The provider key
   lives only in the OS keyring, and every resolved address is validated
   before connecting.
7. **System rollback never restores mutable home data.** User data has its
   own snapshot tool.
8. **Containers cannot prove a kernel, bootloader or GPU driver works.**
   Use VMs, then staged real boots on hardware.

## Core API (all endpoints require authentication)

`GET /health`, `GET /system`, `GET /providers`, `POST /providers/select`,
`POST /chat`, `GET|DELETE /history`, `GET /inference/status`,
`POST /inference/unload`, `GET /inference/telemetry`,
`POST /guardian/preview`, `POST /guardian/propose`,
`POST /guardian/submit`, `GET /guardian/candidates/{id}`,
`POST /guardian/review`, `GET|POST|DELETE /search/sources`,
`GET /search/status`, `POST /search/reindex`, `DELETE /search/index`,
`POST /search`.

## Local search index (M8, feature 8)

Private search finds files by words and by meaning without anything
leaving the machine. Code: `SearchSources.cs`, `SearchExtraction.cs`,
`SearchEmbeddings.cs`, `SearchIndex.cs`, `SearchIndexer.cs`,
`SearchEndpoints.cs`.

**Only chosen folders.** Nothing is indexed until the person adds a folder
(`POST /search/sources {paths}`, `nexus search sources add <dir>`). The list
is stored in Core's settings table. A folder is refused unless it is an
absolute path inside the user's home that exists, is reached without a
symbolic link, is not Nexus's state directory, and has no hidden component
(`.ssh`, `.gnupg`, `.local/share/keyrings`, `.mozilla`, `.config/…`) and no
credential or browser-profile name (`keyrings`, `google-chrome`, `chromium`,
`BraveSoftware`, …). One refused path means nothing in the request is added.
The indexer applies the same rule to every folder it walks, skips hidden
files, never follows a symbolic link, opens each file with
`O_NOFOLLOW|O_NONBLOCK` and indexes regular files only (a FIFO cannot block
it). Each scan re-validates the chosen folders; one that disappears or turns
into a link has its entries dropped.

**What is indexed.** Text (`.txt .md .rst .org .tex .adoc`), data (`.csv .tsv
.json .yaml .toml .xml .ini`), code (about 50 source extensions) and
documents (`.docx`, `.odt`, read from the archive's XML with DTDs refused and
every part size-capped). Known but unsupported documents (`.pdf .doc .rtf
.xlsx .pptx …`) are reported as "not indexed (pdf)" and so on; other files
(images, binaries, files with no extension such as `id_rsa`) are ignored.
Files over `NEXUS_SEARCH_MAX_FILE_BYTES` (4 MiB) or with a NUL byte are
skipped. Text is cut into passages of about 1,000 characters at line
breaks; each records its path, mtime, size, character offset and lines.

**Storage.** `search.sqlite3` in the state directory (0600 in the 0700
directory; a symlinked file is refused), separate from history so it can be
erased alone. Passages are in an FTS5 table (`porter unicode61`, file name
weighted above body); embeddings are float16 unit vectors keyed by passage.
`secure_delete` is on, and after deletions FTS5 is optimised so removed words
leave the file rather than lingering as tombstones.

**Embeddings stay local.** Vectors come from Ollama's `/api/embed` at
`NEXUS_OLLAMA_URL` (`NEXUS_EMBED_MODEL`, default `nomic-embed-text`; `off`
disables). The URL passes the same loopback-only guard as the chat provider,
uses the same no-proxy, no-redirect HTTP client, and the embedder never goes
through `ModelRouter`, so a cloud chat provider, even with egress consent, is
never used for indexing or queries. If embeddings fail (Ollama down, model not
pulled), indexing and search carry on with words alone: `GET /search/status`
shows `embeddings.available=false` with the reason, search answers
`mode: "text"` with a notice, and the next scan fills in missing vectors.

**Indexing.** A background service scans on a timer
(`NEXUS_SEARCH_SCAN_SECONDS`, default 300) and after `FileSystemWatcher`
(inotify) events, debounced; the watcher only requests a scan, and the scan
compares mtime and size with the index, so adds, changes and deletes share one
path and the timer covers anything a watcher misses. Files are processed by
`NEXUS_SEARCH_WORKERS` (default 2) workers while the walk is streamed, so
memory is one directory listing plus the set of paths seen. The index is
capped at `NEXUS_SEARCH_MAX_FILES` (100,000) files and `NEXUS_SEARCH_MAX_CHUNKS`
(100,000) passages, 2,000 per file; a walk that hits a limit never treats
unseen files as deleted. Embedding is batched (16) and capped per scan.
`GET /search/status` gives counts, the last run (indexed, updated, unchanged,
removed, embedded, skipped by reason, with examples) and the last 20 errors;
`POST /search/reindex` forces every file to be re-read.

**Search.** `POST /search {query, limit, kinds}` (1–512 characters, limit
1–50, kinds from `text`, `code`, `data`, `document`) takes up to 16 words,
quoted so FTS syntax is never interpreted, ranks passages by bm25 and, when
embeddings exist, by cosine similarity (brute force over stored vectors,
minimum `NEXUS_SEARCH_MIN_SIMILARITY`, default 0.5), and merges the two by
reciprocal-rank fusion, one result per file. Each result has the path, file
name, kind, a snippet, the modified time, the lines and a `why` sentence built
only from what matched: the person's own words that FTS found in the passage,
and the similarity, for example `Contains "supplier", "prices", in lines
1–3.` No model writes or summarises results. Search is rate-limited to 60 a
minute and two at a time.

**Erasure.** `DELETE /search/index` (`nexus search erase`) cancels any scan,
forgets the chosen folders (so nothing is re-indexed until the person chooses
again), drops every table and `VACUUM`s, then verifies the tables are empty
and the file has no free pages; tests check the indexed text no longer
appears in any `search.sqlite3*` file. Removing a folder
(`DELETE /search/sources?path=…`, `nexus search sources rm <dir>`) deletes its
passages and vectors and optimises FTS, and tests check its text has left the
file too. Like
history, this is erasure at the SQLite level; SSD or filesystem copies are
outside what an application can guarantee.

**Not yet.** Mail, notes, chats and settings as sources; PDF text; an
approximate-nearest-neighbour index for very large collections; and the
activity journal, which is not merged. The points where search reads should
be journalled are marked `ACTIVITY JOURNAL HOOK` in `SearchIndexer.cs` (each
file read) and `SearchEndpoints.cs` (each search).
