# AGENTS.md

WebDavSync — two-way folder sync with a WebDAV cloud, a tray app for Windows 11 on .NET 10.
Replaces a discontinued cloud desktop client that had true two-way folder sync.

Full specification: [spec/](spec/README.md) — one file per section, `§N.M` = section M of `NN-*.md`.
Read the relevant section before changing sync logic; key ones:
[sync algorithm](spec/06-sync-algorithm.md), [safety guards](spec/09-safety-guards.md),
[edge cases](spec/13-edge-cases.md), [legacy client state](spec/03-legacy-state.md).

## Layout

- `src/WebDavSync.Core/` — engine: scanners, WebDAV client, planner, executor, state DB. No UI references.
- `src/WebDavSync.App/` — WinForms tray app (`net10.0-windows`).
- `src/WebDavSync.Cli/` — headless tool: `probe`, `import`, `scan`, `plan`, `sync --once [--dry-run]`.
- `tests/` — unit tests (planner, hash, names, importer) and integration tests (local WebDAV server).

## Storage

- App data (settings, DPAPI secrets, logs) — next to the executable in `<app dir>\data\`.
- Sync state of a pair (`state.db`, backups, temp downloads, trash fallback) — inside the synced
  folder in `<local root>\.webdavsync\` (hidden, never synced). Details: [§15](spec/15-config-storage.md).

## Commands

```
dotnet build
dotnet test
dotnet run --project src/WebDavSync.Cli -- plan --dry-run
```

## Safety invariants (never break these)

1. A failed, partial or timed-out listing is **never** interpreted as deletions.
2. An item is deleted on a side only if it is unchanged there vs the base.
3. Local deletes/overwrites go to the Recycle Bin (or state-dir trash), never hard delete.
4. Before the first full successful cycle (initial sync) no deletes are planned — union merge only.
5. Mass-delete / mass-overwrite guard applies in every mode, including `Auto`.
6. Root sanity check (`.webdavsync\pair.json` with the expected pair id on both sides) before every cycle; missing root → stop, never "sync the emptiness".
7. Base is updated only after an operation is verified on the target side.
8. Downloads go to a temp file and are atomically moved into place; never write the target in place.
9. Legacy client files `.cloud.db` and `.cloud_ss` are read-only for us: never modified, synced or deleted.
   Import works on a copy.
10. Excluding a folder from sync never deletes anything on the remote; re-including it runs in union mode.
11. Planner is a pure function `(Local, Remote, Base, Settings) → Plan`; keep I/O out of it.

## Legacy client state

- `.cloud.db` is SQLite: two trees (local `l_root_id`, remote `r_root_id`) in `FOLDERS_RECORDS` /
  `FILES_RECORDS`, linked by `CLOSURE_TABLE` (parent → direct child, `child_is_folder` picks the table).
  `key` is lower-cased `name`; `inode` is the NTFS file id; `time` is Unix seconds; `hash` is the content hash.
- Content hash: `size < 21` → bytes zero-padded to 20, uppercase hex; else `SHA1("mrCloud" + content + decimal size)`.
- Details: [§3](spec/03-legacy-state.md).

## Conventions

- C# latest, nullable enabled, warnings as errors, file-scoped namespaces, async all the way with `CancellationToken`.
- Code, comments, commit messages and docs in English.
- Every planner rule and edge case from [§6.2](spec/06-sync-algorithm.md) / [§13](spec/13-edge-cases.md) gets a unit test.
- Do not test against the real `D:\Cloud` or the real cloud from automated tests; use temp folders and a local WebDAV server.
- When behavior differs from the spec, update the matching file in `spec/` in the same change.
