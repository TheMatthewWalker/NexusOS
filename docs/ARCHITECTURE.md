# NexusOS Architecture Decision Record 001

## Goals
AI is the primary *optional* interaction channel. Linux stays usable when inference or the internet is unavailable. Model output cannot authorize its own privileged actions.

## Planned components
- `Nexus.Core`: unprivileged user service; intent orchestration, model provider registry, context, events.
- `Nexus.Inference`: llama.cpp and other runners, model memory budgets, hibernation, health checks.
- `Nexus.Guardian`: separate restricted broker, typed operations, policy, diff previews, VM and hardware validation, rollback.
- `Nexus.Desktop`: Quickshell/Hyprland, command palette, settings and provider widget.
- `Nexus.Search`: indexed files with per-user permission enforcement.
- `Nexus.Skills`: capability-scoped tools and marketplace signing.

## Trust boundaries
1. LLM text is untrusted data. No direct root shells.
2. Guardian validates typed plans outside the LLM; require explicit approvals for destructive changes.
3. Cloud routing requires per-scope consent and redaction review.
4. Recovery must work offline without an LLM, independent of mutable user storage.
5. Containers cannot prove a new kernel, bootloader or GPU driver works; use VMs plus staged real boot.
6. System rollback does not restore mutable home data; use snapshots/backups separately.

## First implementation
Core binds exclusively to loopback and exposes `/health`, `/providers`, `/providers/select`, `/chat`, `/system`. The initial single-user API is not remotely accessible by default but has no client authentication. Do not expose it publicly. Model routing and errors are deliberately simple.
