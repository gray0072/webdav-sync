# WebDavSync — Specification

Status: **Draft v0.1** (2026-10-16)
Target: .NET 10, Windows 11 (x64)
Primary sync pair: local `D:\Cloud` ⇄ WebDAV cloud (provider profile: [§4](04-remote-webdav.md))

## Contents

1. [Overview](01-overview.md) — purpose, goals, non-goals, terminology
2. [Architecture](02-architecture.md)
3. [Legacy client state (`.cloud.db`, `.cloud_ss`)](03-legacy-state.md)
4. [Remote (WebDAV) model](04-remote-webdav.md)
5. [Local model](05-local.md)
6. [Sync algorithm](06-sync-algorithm.md)
7. [Scenarios](07-scenarios.md)
8. [Timing — "optimal, not instant"](08-timing.md)
9. [Safety guards](09-safety-guards.md)
10. [Operating modes (debug / approval)](10-modes-approval.md)
11. [Bandwidth, concurrency, pause](11-bandwidth-pause.md)
12. [Filters and selective sync](12-filters.md)
13. [Edge cases (must be handled and covered by tests)](13-edge-cases.md)
14. [Tray UI](14-tray-ui.md)
15. [Configuration and storage](15-config-storage.md)
16. [Testing and rollout](16-testing-rollout.md)
17. [Milestones](17-milestones.md)
18. [Open questions (resolve with `wds probe` / manual checks)](18-open-questions.md)
19. [Prototype readiness](19-prototype-readiness.md) — scope, defaults, what is still missing

Section numbers (§N.M) match file numbers: §6.2 is section 2 of `06-*.md`.
