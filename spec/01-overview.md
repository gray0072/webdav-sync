# 1. Overview

A reliable, transparent, two-way folder synchronizer that replaces the discontinued
desktop client of the cloud provider (Mail.ru Cloud, the old version with true bidirectional folder sync).

It lives in the system tray, syncs a local folder with a WebDAV remote, and makes its
state obvious: idle, working, paused, waiting for approval, conflict, error.

## 1.1 Goals

- Two-way sync with correct handling of creates, edits, deletes, renames and moves on both sides.
- **Safety first**: never lose user data. Prefer duplicates over losses. Every destructive
  action is reversible (Recycle Bin / cloud trash) or explicitly approved.
- **Debug / approval mode** for the first runs: see and approve the plan before anything is touched.
- Compatibility with the legacy client's state (`.cloud.db`): reuse it as the initial
  baseline so an already-synced 44k-file folder does not need a full re-download or re-upload.
- Batched, "optimal" (not instant) sync: wait for changes to settle, coalesce them, and keep
  request rate and bandwidth under control.
- Bandwidth limits, concurrency limits, pause/resume, schedule.
- Selective sync: choose which top-level folders are synced, changeable on the fly ([§12.2](12-filters.md)).
- Tray UI with progress, errors, conflicts and pending approvals.

## 1.2 Non-goals (v1)

- Placeholder / on-demand files (Cloud Files API, "Files On-Demand").
- Multiple accounts in one UI (the design must not preclude multiple sync pairs, but v1 UI ships with one).
- Sharing, public links, versions browser.
- Non-Windows platforms (Core library stays platform-neutral where cheap).
- Native provider APIs as a replacement of WebDAV. Keep `IRemoteStore` abstract so it can be added later (see
  [§2.3](02-architecture.md)); the Mail.ru API is used for reads only (tree listing, change token, [§4.7](04-remote-webdav.md)).

## 1.3 Terminology

| Term | Meaning |
|---|---|
| **Pair** | One local root + one remote root + its state DB and settings. |
| **Local (L)** | Current state of the local tree. |
| **Remote (R)** | Current state of the remote tree. |
| **Base (B)** | Last state known to be identical on both sides (per item). Stored in the state DB. |
| **Cycle** | One run of: collect changes → plan → (approve) → execute → commit base. |
| **Plan** | Ordered list of operations produced by the planner for one cycle. |
| **Operation (Op)** | Atomic unit: `Upload`, `Download`, `MkdirLocal`, `MkdirRemote`, `MoveLocal`, `MoveRemote`, `DeleteLocal`, `DeleteRemote`, `Conflict`, `UpdateBase`. |
| **Content hash** | SHA1-based hash used by the legacy client and the cloud ([§3.3](03-legacy-state.md)). |
| **Key** | Case-folded relative path used for matching (Windows and the cloud are case-insensitive). |
