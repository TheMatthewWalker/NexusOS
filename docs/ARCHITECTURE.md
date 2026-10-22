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
| `Nexus.Desktop` | Not started (M6) | Hyprland/Quickshell command palette, provider widget, change-proposal view |
| `Nexus.Search`, `Nexus.Skills` | Not started (M8/M9) | Permission-aware indexing; capability-scoped, signed tools |

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
`POST /guardian/review`, `POST /try/keep`.

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
