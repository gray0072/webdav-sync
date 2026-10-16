# 15. Configuration and storage

Two places, by ownership:

- **App data** lives next to the executable, in `<app dir>\data\` — everything about the
  application and the user's setup (portable: copy the app folder and the setup moves with it).
- **Sync state** lives inside the synced folder, in `<local root>\.webdavsync\` — everything
  about one pair's synchronization (like the old client's `.cloud.db`; the state travels with the folder).

## 15.1 App data — `<app dir>\data\`

```
data\
  settings.json        app settings + list of pairs (pair id, local root, remote URL, login, mode, limits, ...)
  secrets.dat          app passwords, DPAPI-encrypted (CurrentUser scope), keyed by pair id
  logs\                rolling log files
  diagnostics\         exported diagnostics bundles
  app.lock             single-instance lock (in addition to the named mutex)
```

- The app dir must be writable (portable install, not `Program Files`). If it is not
  writable → clear error at startup and exit; no silent fallback to `%APPDATA%`.
- `secrets.dat` is bound to the Windows user (DPAPI): copying the app folder to another
  user/machine requires re-entering the password. Never stored in plain text, never logged.
- Logs: rolling daily files, 30 days, default level Information; Debug toggle in Settings.
  Each op logged with: pair id, cycle id, op id, type, path(s), sizes, reason, result, duration.
- "Export diagnostics" bundles logs + state stats (no file contents, no credentials).

## 15.2 Sync state — `<local root>\.webdavsync\`

```
<local root>\.webdavsync\     hidden folder, never synced
  pair.json            pair id, created at, remote URL, volume serial at setup
  state.db             SQLite (WAL): base tree, journal, conflicts, held, meta
  state.db-wal, -shm   SQLite WAL files
  backup\              daily copies of state.db (keep 3), via SQLite online backup API
  tmp\                 partial downloads (same volume → atomic move into place)
  trash\<date>\        fallback when the Recycle Bin cannot take an item (§9)
  engine.lock          held while an engine runs this pair
```

- The folder gets the `Hidden` attribute; it is excluded from scanning, watching and syncing
  (built-in filter, [§12](12-filters.md)). Watcher events under it are dropped early.
- Remote counterpart: `<remote root>/.webdavsync/pair.json` (same pair id) — the remote root marker.
  Nothing else of the state goes to the remote.
- `settings.json` and `pair.json` must agree on the pair id; mismatch → the pair stops with an error
  (e.g. a copied folder, or a folder from another setup).
- State missing in an existing non-empty root → treated as a new pair: initial sync in union
  mode, no deletes ([§7.6](07-scenarios.md)). The user is warned that the state was lost.
- State is self-contained and portable with the folder: if the folder moves to another path or
  volume, the app asks to confirm the new location; FileIds from the old volume are discarded
  (rescan and re-hash by size/mtime), hashes and base stay valid.
- Corrupted `state.db` (integrity check fails on open) → stop, offer restore from `backup\`
  or reset to union-mode initial sync.

## 15.3 State DB (sketch)

```
items(key PK, path, is_dir, local_file_id, local_size, local_mtime, local_hash,
      remote_etag, remote_size, remote_mtime, remote_hash, synced_at)
journal(op_id PK, cycle_id, type, path, path2, status, attempts, error, created_at, updated_at)
conflicts(id PK, key, local_copy, remote_copy, detected_at, resolved_at)
held(key PK, reason, held_at)
meta(key PK, value)   -- schema version, pair id, capabilities, initial_sync_done, legacy_import info
```

## 15.4 Legacy files

`.cloud.db` and `.cloud_ss` stay in the root next to `.webdavsync\`, untouched ([§3.4](03-legacy-state.md)).

Open question: should we additionally maintain a `.cloud.db`-compatible writer (to keep the
legacy files meaningful)? Default answer for v1: no; the legacy file is imported once and left untouched.
