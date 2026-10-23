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
| `nexus-try` (`nix/packages/nexus-try.sh`, `nix/modules/try-apps.nix`) | Implemented | Runs a nixpkgs app in a throwaway bubblewrap sandbox; keeping it is a Guardian proposal |
| `Nexus.Desktop` (`shell/`, `nix/modules/desktop.nix`) | In progress (M6) | The Hyprland session and the Nexus shell (Quickshell, ADR-003), comprising the wallpaper, launcher, Nova core, app orbit, chat and system panel. It reaches Core only through `nexus bridge` |
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
8. **The desktop shell holds no secrets and gets no privileges.** It runs as
   the user, reaches Core only through the `nexus bridge` CLI, renders model
   output as plain text, and sits below lock screens. App suggestions name
   catalogue roles only; opening an app is always the user's action.
9. **Containers cannot prove a kernel, bootloader or GPU driver works.**
   Use VMs, then staged real boots on hardware.

## Rules engine (automations in plain words)

Feature 9. A rule is data in a fixed schema, kept in the `rules` table of
Core's `state.sqlite3` (added by the `schema_rules_v1` migration, which
commits with its marker in one transaction):
`{id, name, enabled, trigger, conditions[], actions[], createdFrom, history}`.

- **Fixed lists.** Triggers: `device.connected` / `device.disconnected`
  (kind `audio`, `display` or `usb`, optional part of the device name),
  `time.daily` (`HH:MM`, optional days), `app.opened` (app id),
  `network.joined` (SSID), `power.plugged` / `power.unplugged`. Conditions:
  `time.between`, `time.days`, `power.is`. Actions: `app.open` (app id),
  `setting.set` for the reversible personal settings `volume`,
  `notifications.volume`, `night-light`, `do-not-disturb` and `wallpaper`
  (a name from the wallpaper library, never a path), `notify` (text),
  `backup.run` (profile). At most 4 conditions and 5 actions. Anything else
  is rejected; one validator reads model output, API input and stored rows.
- **No system changes.** The schema has no command, package, service or file
  action. A sentence or model output asking for one (or naming an
  interpreter or privileged tool as the app) is refused with "System changes
  go through approval"; those stay Guardian proposals. A rule that opens the
  app that triggers it is rejected.
- **Parsing.** `POST /rules/parse {sentence}` returns a draft and saves
  nothing. System-change sentences are refused before the model is asked.
  Otherwise the selected model (same cloud consent as chat) gets the lists
  and the fenced sentence and must reply with one strict JSON rule or a
  refusal code (`system-change`, `unsupported`); free text is never accepted.
  If the model is unavailable or its output is invalid, a small set of
  keyword patterns is tried (`source: keywords-fallback`); if they do not
  understand every part, the answer is "Couldn't turn that into a rule" with
  suggestions. The sentence shown on the card is rendered by Core from the
  typed rule, never taken from model text.
- **Saving and managing.** `POST /rules` saves a confirmed rule (re-validated;
  at most 100). `GET /rules`, `POST /rules/{id}/pause|resume`,
  `DELETE /rules/{id}` (removes its history too), `GET /rules/{id}/history`.
  Like every Core endpoint, all of these need the bearer token.
- **Running.** The shell reports what it observes with `POST /events`
  (device, app, network and power events; same field rules as triggers;
  120 a minute). Core matches enabled rules, checks conditions (an unknown
  power state never counts as met), records a run and returns the actions in
  the response for the shell to perform: Core cannot open apps or change
  shell settings. A 20-second timer fires `time.daily` rules in local time
  (once a day, within 2 minutes of their time) and queues their actions for
  `GET /rules/pending-actions?after=<cursor>`; queued actions expire after
  5 minutes rather than run late. Each rule fires at most once a minute, so
  flapping devices or rules that open each other's apps cannot loop.
- **Run history follows chat history.** Runs older than
  `NEXUS_HISTORY_MAX_DAYS` are hidden and pruned; `NEXUS_HISTORY_LIMIT` caps
  runs per rule (0 records none), with a hard cap of 10,000; `DELETE
  /history` erases every run (rules stay). Rule contents are never logged.
- **CLI:** `nexus rules`, `nexus rules add "<sentence>" [--yes]` (shows the
  draft and asks before saving), `nexus rules pause|resume|rm <id>`,
  `nexus rules history <id>`.

## Core API (all endpoints require authentication)

`GET /health`, `GET /system`, `GET /providers`, `POST /providers/select`,
`POST /chat`, `POST /chat/stream` (NDJSON pieces, then `done` or `error`),
`POST /suggest` (catalogue rules), `POST /suggest/model` (the model picks from
the catalogue and falls back to the rules, labelled as such), `GET /apps`,
`GET|DELETE /history`, `GET /inference/status`,
`POST /inference/unload`, `GET /inference/telemetry`,
`POST /guardian/preview`, `POST /guardian/propose`,
`POST /guardian/submit`, `GET /guardian/candidates/{id}`,
`POST /guardian/review`, `POST|GET|DELETE /journal`,
`GET /journal/today/summary`, `POST /journal/{id}/undo`, `POST /fixit`, `POST /fixit/apply`,
`GET /health/report`, `GET|POST|DELETE /search/sources`,
`GET /search/status`, `POST /search/reindex`, `DELETE /search/index`,
`POST /search`, `POST /try/keep`.

## Activity journal

One per-user, append-only log of what happened, in the `journal` table of
Core's `state.sqlite3` (added by the `schema_journal_v1` migration, which
commits with its marker in one transaction). Each entry has a UTC time, a
kind (`setting`, `app`, `system`, `ai-read`, `egress`, `device`), an actor
(`user`, `nexus` or an app name), a one-line summary, and optional
`before`/`after` JSON, `reversible` flag and `ref`.

- **Core records what it observes.** Every cloud request is journalled as
  `egress` just before it is sent, with the provider, host and consent time
  (when the person selected the cloud provider; the operator's
  `NEXUS_CLOUD_EGRESS_ALLOWED` is the precondition). It never holds prompt or
  answer text. Guardian proposals, submissions and the broker's decisions are
  `system` entries. Core reads no files or context to answer today, so it
  writes no `ai-read` entries yet.
- **The shell records the rest.** `POST /journal` accepts only `setting` and
  `device` entries, never as actor `nexus`; summaries are at most 200
  characters, `before`/`after` at most 4 KB each, 60 writes a minute.
- **Undo** (`POST /journal/{id}/undo`) applies to reversible `setting` entries
  with a `before` value. Core cannot change shell settings, so it returns the
  value to restore, marks the entry undone, and refuses a second undo.
- **Retention and erasure follow history.** Entries older than
  `NEXUS_HISTORY_MAX_DAYS` are hidden and pruned by both history and journal
  writes, with a hard cap of 10,000 rows. `DELETE /journal?day=YYYY-MM-DD`
  ("forget today") deletes that local day's entries with `secure_delete`, and
  checks none remain before committing. `GET /journal/today/summary` gives the
  privacy dashboard's counts: left the computer, AI reads, device uses.
- **CLI:** `nexus journal [--today] [--kind K]`, `nexus journal forget-today`.

## Probes and Fix it

Design: [features.md](design/features.md) ("Probes", features 2 and 14).

- **Probes** (`Probes.cs`) are a fixed, in-code allow-list of read-only
  diagnostics: an id, an area (sound, network, display, power, disk, app),
  an exact argv started without a shell, a timeout and an output cap.
  Binaries resolve only from fixed system directories (`NEXUS_PROBE_PATH`
  overrides them); a missing tool reports `unavailable`, never a guess.
  Output is untrusted text, parsed into bounded records.
- **Fix library** (`src/NexusOS.Core/fixes/*.json`, embedded and validated
  at startup) holds known problems as data: an area, match clauses (a regex,
  structured field conditions, or a probe status), a plain-English cause and
  one or two fixes. A `personal` fix names an action from the in-code
  allow-list (`PersonalActions`), whose parameters are bound from probe
  records and must match a strict pattern. A `system` fix is only a
  description for a Guardian proposal.
- **`POST /fixit`** classifies the area by keywords, or (only when no
  keyword matches, and only with a local provider) asks the model for
  `{"area": ...}` under a strict schema; anything else falls back to checking
  every area. The model never sees probe output and never chooses a fix or
  command. Core runs the area's probes, matches the library and returns the
  cause, short sanitised evidence excerpts, the fixes and a one-use evidence
  token.
- **`POST /fixit/apply`** runs a personal fix only if it belongs to the latest
  diagnosis and the token matches and is under 10 minutes old (410 when
  stale, 404 for a fix not in that diagnosis). System fixes return 409
  "needs approval" and are never applied by Core.
- **`GET /health/report`** runs the health probes and fills fixed templates
  with validated figures, plus any library problems found. No model.

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

**What is indexed.** `GET /search/status` (`formats`) and `nexus search
formats` list the types. All extractors are pure .NET except PDF, which uses
[PdfPig](https://github.com/UglyToad/PdfPig) (Apache-2.0, no dependencies on
net8.0). None touches the network.

| Kind | Types | What is extracted |
|---|---|---|
| text | `.txt .md .rst .org .tex .adoc` | The text (UTF-8) |
| code | about 50 source extensions | The text |
| data | `.csv .tsv .json .yaml .toml .xml .ini` | The text |
| data | `.xlsx .xlsm`, `.ods` | Each sheet as `Sheet: name`, one row per line, cells tab-separated; shared strings resolved, cached values used (formulas not evaluated), repeated ODS cells not expanded |
| document | `.docx`, `.odt` | Paragraph text |
| document | `.pptx`, `.odp` | `Slide n`, its text in presentation order, then `Notes: …` (speaker notes); slide-number fields skipped |
| document | `.pdf` | The text layer, page by page in reading order. A PDF with no text (a scan) is reported as `not indexed (pdf: no text layer)` and is never OCR'd; encrypted PDFs as `(pdf: encrypted)` |
| document | `.rtf` | Text with control words removed; font and colour tables, pictures, metadata, headers, footers and field instructions dropped; `\'hh` and `\u` escapes decoded |
| document | `.epub` | Title, then each XHTML chapter in spine (reading) order |
| document | `.html .htm .xhtml` | Visible text; scripts, styles, templates, SVG, objects and comments dropped; charset from a BOM or `<meta charset>` |
| mail | `.eml` | `Subject`, `From`, `To` and `Date` (RFC 2047 decoded), then the text body (plain text preferred over HTML in `multipart/alternative`); attachments are listed by name only and never decoded |

Legacy binary `.doc .xls .ppt` (and Apple `.pages .numbers .key`) are
reported as `not indexed (doc)` and so on: there is no small, maintained,
pure-.NET reader for OLE2 files, and parsing them would add attack surface
for little gain. Other files (images, binaries, files with no extension such
as `id_rsa`) are ignored.

**Extraction bounds.** Every file is size-capped (`NEXUS_SEARCH_MAX_FILE_BYTES`,
4 MiB) and its extracted text capped (4 Mi characters). Zip-based formats
(docx, xlsx, pptx, odt, ods, odp, epub) are refused when they have more than
10,000 entries or declare more than 64 MiB unpacked, and every entry is read
through one shared budget of real decompressed bytes, so headers that lie
about sizes cannot get past it. XML is read with DTDs prohibited, so there is no
entity expansion. PDF streams are counted against the same 64 MiB budget, and
Flate streams are inflated into a counter first, so a compression bomb is
refused before PdfPig allocates its output; PDFs stop at 2,000 pages. Sheets
stop at 200,000 cells, and RTF nesting at 512 groups. Each file has a time limit
(`NEXUS_SEARCH_EXTRACT_SECONDS`, default 30); a slow extractor is cancelled
and the scan moves on. Any failure on a malformed or hostile file is reported
as `not indexed (type: unreadable)` (or `too large when unpacked`, `timed
out`) and never fails the scan. Text is cut into passages of about 1,000
characters at line breaks; each records its path, mtime, size, character
offset and lines.

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
1–50, kinds from `text`, `code`, `data`, `document`, `mail`) takes up to 16 words,
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

**Not yet.** Mailboxes (beyond `.eml` files), notes, chats and settings as
sources; OCR for scanned PDFs; legacy binary Office files; an
approximate-nearest-neighbour index for very large collections; and the
activity journal, which is not merged. The points where search reads should
be journalled are marked `ACTIVITY JOURNAL HOOK` in `SearchIndexer.cs` (each
file read) and `SearchEndpoints.cs` (each search).

## Try before you install

`nexus-try <attr> [-- args]` (also `nexus try <attr>`) runs an app from the
system's pinned nixpkgs without installing it. `services.nexus-desktop.tryApps.enable`
(default `true`, imported by the Core module) puts `nexus-try` and
`bubblewrap` on the system.

1. **Fetch, don't install.** The attribute must be a plain nixpkgs attribute
   path (`hello`, `python3Packages.ipython`): no flake references, paths,
   quoting or shell metacharacters. It is fetched or built with
   `nix build --no-link --print-out-paths nixpkgs#<attr>` through the system
   registry. No profile is touched and no GC root is left, so the next
   garbage collection removes it. When the registry entry is locked (it has
   a `narHash`, as on flake-built systems), evaluation is cached after the
   first trial.
2. **Run in bubblewrap**, as the user, with:
   - a new user, PID, IPC, UTS, cgroup and (with `--no-network`) network
     namespace, `--die-with-parent`, `--new-session` and all capabilities
     dropped;
   - an empty root, with `/nix/store` and a short list of `/etc` files
     (passwd, hosts, resolv.conf, certificates, fonts, time zone, audio and
     GPU config) read-only, a minimal `/dev` plus `/dev/dri`, and read-only
     `/sys` device trees for GPU probing;
   - `$HOME` as an empty directory on the sandbox's own tmpfs. The real home
     is never mounted and the sandbox home vanishes when the app exits;
   - the Wayland socket, the PipeWire socket and the PipeWire Pulse socket.
     No X11 socket and no D-Bus session bus, so portals, notifications and
     X11-only apps do not work;
   - a cleared environment with only locale, terminal and display variables;
   - with `--file PATH`, that one file read-only at `~/Trial/<name>`.
3. **Keep it.** On exit `nexus-try` says nothing was kept and offers
   `nexus try keep <attr>`. That calls `POST /try/keep {attr}`, which builds
   the same typed plan as any "install `<attr>`" request (a `{"package": attr}`
   Guardian change that renders to `environment.systemPackages = [ pkgs.<attr> ];`)
   and submits it to the broker. The normal Guardian path follows: root
   approval, a signed builder attestation and a trial boot. Core never
   installs anything.

### Security model

The sandbox stops a trialled app from reading or changing the user's files,
other users' files and the user's real configuration, from keeping anything
after it exits, from seeing other processes, and (with `--no-network`) from
reaching the network. It is a convenience boundary for trying software, not a
hard security boundary. It does not protect against:

- **Kernel exploits.** It relies on unprivileged user namespaces, so a kernel
  bug reachable from a sandbox can escape it. There is no seccomp filter.
- **The sockets it is given.** A Wayland client can draw convincing windows
  and read what the compositor gives it (its own input, the clipboard on
  paste). PipeWire access includes the microphone and other streams, because
  the main socket is not filtered per app.
- **The network**, which is on by default: the app can reach the internet and
  local network services the user can reach, including loopback services.
- **Information in what is shared:** `/etc/passwd`, hardware details under
  `/sys` and `/dev/dri`, and the whole world-readable `/nix/store`.
- **Malicious builds.** The app is whatever nixpkgs builds or the binary cache
  serves for the pinned revision; the sandbox limits what it can do, not what
  it is.
- **Resource abuse.** There are no CPU, memory or tmpfs size limits, so an
  app can still exhaust the session.
- **Store garbage collection.** `--no-link` leaves no GC root, so a GC during a
  trial can remove files the running app needs.

Keeping an app gets no extra trust from the trial: it is reviewed and staged
like any other Guardian change.
