# NexusOS feature designs

Fourteen features approved on 22 Oct 2026, with how each one works. Mocks are on
the [design canvas](https://claude.ai/artifact/R9EACfTrFZgo8924JqNVTQ) (row
"Features"). Every feature keeps the binding rules in
[spaceui/README.md](spaceui/README.md#rules-that-bind-every-implementation):
the AI suggests and the person decides, system changes go through Guardian,
nothing is faked, and private data stays on the machine.

## The building blocks they share

Most features are new views over things NexusOS already has, plus four new
pieces of plumbing. Build the plumbing once and most features become small.

| Block | What it is | Status |
|---|---|---|
| **System generations** | NixOS keeps every system version; systemd-boot lists them; Guardian stages new ones with a fallback boot | Built (M3/M4) |
| **Guardian proposals** | A typed, signed, previewed system change, approved by the person, staged and health-checked | Built (M2–M4), desktop dialog approved |
| **Core** | Per-user AI daemon: chat, suggestions, history with retention, model routing (local first) | Built (M1, M6) |
| **Activity journal** *(new)* | One per-user, append-only log of *what happened*: settings changed, apps installed, files the AI read, data that left the machine. Every feature that shows or undoes history reads it | To build: SQLite table in Core's per-user database, same retention and erase rules as history |
| **Local index** *(new)* | On-device search index over files, mail, notes, chats and settings: full-text plus embeddings from a small local model | To build (M8) |
| **Probes** *(new)* | Read-only, allow-listed diagnostics Core may run: `journalctl` for the user's units, `wpctl status`, `nmcli`, `upower`, `smartctl` (through Guardian for root-only reads), disk usage. Never shell commands chosen by the model | To build |
| **Rules engine** *(new)* | Triggers (device plugged, time, app opened, network joined) → actions (open app, change a reversible setting, run a backup). Stored as data, shown as plain sentences | To build (M9) |

## Making it easy

### 1. Undo timeline
- **What you see:** Settings → Timeline. Every change appears as a point with a plain label: "Installed Blender (Tue 14:02)", "Night light on", "System update". Each has **Undo**, and a system point has **Go back to this**.
- **How it works:** personal settings changes come from the activity journal, each storing its old value, so undo writes it back. System points are NixOS generations, labelled from Guardian's proposal summary. "Go back to this" is itself a Guardian proposal: a staged boot into that generation, with the usual health check and fallback.
- **Limits:** it never undoes your files. Deleted files are the backup feature's job (E3 backup already exists), and the timeline links to it.
- **Effort:** small once the journal exists; the generation half can ship first.

### 2. Fix it
- **What you see:** a **Fix it** button in the system panel, or "my sound stopped" said to Nexus. Nexus shows the cause in one sentence, the evidence it used, and one or two fixes.
- **How it works:**
  1. Core classifies the problem area (sound, network, display, app, disk).
  2. It runs that area's probes; they are read-only and allow-listed.
  3. The local model gets the probe output, never raw access, and matches it against a small library of known problems and fixes kept in the repo as data.
  4. A personal fix (restart PipeWire, reconnect Wi-Fi) runs on Apply. A system fix (missing firmware, a driver option) becomes a Guardian proposal.
- **Safety:** probe output is treated as untrusted text (prompt-injection tests already exist for plans), and the model only picks from known fix templates.
- **Effort:** medium; it grows by adding problems to the library.

### 3. Plain-language system state
- **What you see:** hover any planet in the system panel to get a sentence, not a number. For example: "About 3 h left at this brightness". Or: "40 GB free; Downloads is using 18 GB. Show the biggest files?"
- **How it works:** the panel already reads real state (PipeWire, NetworkManager, UPower). A small formatter turns readings into sentences; estimates such as battery time use UPower's own figures, never invented ones. Suggestions open the right app or settings page.
- **Effort:** small; it's shell-only.

### 4. First-run setup by conversation
- **What you see:** after the first login, the core asks "What will you mostly use this computer for?" and offers chips: Studying, Gaming, Office, Coding, Creative, Just browsing. Nexus proposes a bundle of apps and settings as **one Guardian proposal**, with every item shown and removable.
- **How it works:** the bundles are curated data in the repo (`nix/bundles/*.nix`): package sets plus settings. The model maps free text to bundles but never invents packages. The proposal is a normal Guardian candidate, so it previews, signs, stages and can be rolled back.
- **Effort:** medium; it needs the bundles and the onboarding screen (M7).

### 5. Move in from Windows or macOS
- **What you see:** "Bring my stuff over". Choose an old drive, a backup folder or another computer on the network, and Nexus lists what it found: documents, photos, browser bookmarks and passwords (exported), and apps with their NexusOS equivalents. You tick what to bring.
- **How it works:** read-only mounting of the old drive, which already supports NTFS, APFS (read) and exFAT. Known locations per OS are data (`Users/*/Documents`, browser profile paths); browser data is imported with each browser's own import format. App equivalents come from a curated map, and installing them is a Guardian proposal.
- **Effort:** large; ship documents and photos first, browsers next, apps last.

### 6. Accessibility from the start
- **What you see:** Settings → Accessibility. It covers voice control of the desktop, a screen reader, **Describe this** (images and screens described by the local vision model), larger text, high contrast, reduced motion and sticky keys.
- **How it works:** the shell already labels its controls for screen readers (`Accessible.*`) and has reduced motion. Orca provides the screen reader. Voice control uses local speech-to-text (whisper.cpp) feeding Nexus intents, so "open Files", "scroll down" and "click Send" act through the same accessibility tree. Describe-this sends a screenshot to the local vision model (M10), never to the cloud unless the person allows it each time.
- **Effort:** a large area; the contrast and labels are done, the screen reader is configuration, and voice is M10.

## Standing out

### 7. Workspaces that remember
- **What you see:** name a workspace ("Dissertation"). Later, "open my dissertation" brings back its windows, files, browser tabs and the Nexus chat you had there. In the Super+Tab overview, each workspace card shows what it holds.
- **How it works:** Hyprland gives the windows and workspaces (IPC). A small per-workspace record in Core keeps the open apps, their files (from the activity journal) and browser session files. Restoring relaunches the apps with those files and moves them back. The chat is tagged with the workspace.
- **Effort:** medium; it builds on the chosen overview (Desktop C).

### 8. Private search across everything
- **What you see:** the Nexus bar searches files, mail, notes, chats and settings **by meaning**. For example: "the spreadsheet with supplier prices from March". Results show where each was found and why it matched.
- **How it works:** the local index (above). File watchers (inotify) keep it current; the person chooses which folders are included, and nothing outside them is indexed. Text plus a small local embedding model gives results by meaning, and the answer cites real files, never a summary of something it didn't read. The index sits in the user's state directory with the same 0700/0600 permissions and erase rules as history.
- **Effort:** large (M8), but it is also the foundation for 5, 7 and 10.

### 9. Automations in plain words
- **What you see:** "When I plug in my headphones, open Spotify and turn notifications down." Nexus shows the rule as a sentence card with its trigger and actions, plus **Pause**, **Edit** and a history of when it ran.
- **How it works:** the model turns the sentence into a rule in a fixed schema (trigger, conditions, actions), from a fixed list of triggers and actions. The rules engine runs them in Core. Actions that would change the system are not allowed in automations; they stay Guardian proposals.
- **Effort:** medium (M9).

### 10. Explain mode
- **What you see:** press Super+? and point at anything (a window, a setting, an error, terminal output). Nexus explains it in plain words, with a **Tell me more** button.
- **How it works:** the shell captures the pointed region (screencopy) and its accessibility text. The local model explains from that text first and uses vision only when there is no text. Terminal output is read as text. Explanations can link to the Fix it library when they spot a known problem.
- **Effort:** medium; it shares parts with Describe this (6).

### 11. Game mode
- **What you see:** start a game and a small toast says "Game mode: AI paused, GPU free". Quit and everything comes back.
- **How it works:**
  1. Detection uses the fullscreen signal already wired in the shell, plus Steam and Proton process names.
  2. Core unloads the model; `nexus unload` and VRAM telemetry already exist (M5).
  3. The shell pauses the core; that's built (#60).
  4. Hyprland switches off blur and animations for the session.
  5. On exit the model reloads lazily, on the next question.
- **Effort:** small; this mostly joins existing pieces and closes the M5 game-launch gate.

### 12. Privacy dashboard
- **What you see:** one page, covering today:
  - what Nexus read (files, screens);
  - what left the computer (normally nothing; each cloud request listed with consent time);
  - which apps used the microphone, camera or location.

  It has a **Forget today** button.
- **How it works:** Core already logs cloud consent and keeps history with retention. The activity journal adds what the AI read. PipeWire reports which apps open the microphone and camera. "Forget" uses the verified erasure Core already has (M2).
- **Effort:** small once the journal exists.

## Trust and safety

### 13. Try before you install
- **What you see:** an app's page has **Try it**. It opens straight away in a sandbox and leaves nothing behind when closed. **Keep it** installs it for real, through Guardian.
- **How it works:** `nix run` fetches the app into the store without installing it. The app runs inside bubblewrap with a throwaway home folder and only the portals it needs, so it cannot see your files unless you hand one over. "Keep it" turns into a normal Guardian proposal.
- **Effort:** medium.

### 14. Health report card
- **What you see:** a weekly card from the core covering updates applied, backups (last run, and whether a test restore worked), disk health (SMART), free space, and anything Guardian blocked and why. Problems link to Fix it.
- **How it works:** a weekly user timer gathers probe results and journal entries. The text comes from fixed templates with real numbers; the model may add a one-line summary but cannot change the figures.
- **Effort:** small once probes exist.

## Order of work

1. **Plumbing:** activity journal, then probes.
2. **Quick wins** on top of it: Undo timeline (1), Plain-language state (3), Game mode (11), Privacy dashboard (12), Health report (14).
3. **Fix it (2)**, then **First-run setup (4)** with the M7 onboarding.
4. **Local index**, then **Private search (8)**, **Workspaces that remember (7)** and **Explain mode (10)**.
5. **Automations (9)**, **Try before you install (13)**, **Move in (5)**, and the voice and vision parts of **Accessibility (6)** (M10).
