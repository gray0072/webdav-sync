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
- `secrets.dat`: JSON `{ "<pair id>": "<base64 DPAPI blob>" }`, entropy `WebDavSync/<pair id>`. CLI:
  `wds secret set <pair> [--from-env]` (hidden prompt), `wds secret remove <pair>`; a pair's password is taken from
  `secrets.dat`, then from `WDS_PASSWORD` (probe and scripts use only the variable).
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
  tmp\                 partial downloads (same volume → atomic move into place); dl-*.part are resumable (§6.6)
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

## 15.3 State DB

```
items(key PK, is_dir, local_path, remote_path, local_file_id, local_size, local_mtime, local_hash,
      remote_etag, remote_size, remote_mtime, remote_hash, remote_confirmed, synced_at)
journal(op_id PK, cycle_id, type, path, path2, status, attempts, error, created_at, updated_at)
conflicts(id PK, key, local_copy, remote_copy, detected_at, resolved_at)
held(key, type, path, reason, state, held_at; PK key + type)
meta(key PK, value)
```

- Times are UTC ticks; `local_file_id` is the 64-bit file index stored as a signed integer.
- `local_path` / `remote_path`: real names per side (case and Unicode normalization may differ, §6.9).
- `remote_confirmed = 0` for entries imported from `.cloud.db` until the first crawl adopts them ([§3.5](03-legacy-state.md)).
- Journal status: `Running`, `Done`, `Failed`, `Skipped`, `Interrupted` ([§6.7](06-sync-algorithm.md)).
- `meta` keys: `schema_version`, `pair_id`, `initial_sync_done`, `remote_marker_created`, `legacy_import`
  (import stats, JSON), `last_cycle` (summary, JSON), `capabilities` (reserved for probe results).
- `held`: the held list ([§10](10-modes-approval.md)); `type` is the op type, `path` its display path, `state` the
  item's state on both sides when it was held. Schema 2; opening a schema-1 DB drops its unused `held` table.

## 15.4 Legacy files

`.cloud.db` and `.cloud_ss` stay in the root next to `.webdavsync\`, untouched ([§3.4](03-legacy-state.md)).

Open question: should we additionally maintain a `.cloud.db`-compatible writer (to keep the
legacy files meaningful)? Default answer for v1: no; the legacy file is imported once and left untouched.

## 15.5 `settings.json` schema

Camel-case JSON, enums as strings. Top level: `{ "pairs": [ <pair>, ... ] }`. Implemented settings of a pair:

| Name | Type | Default | Meaning |
|---|---|---|---|
| `id` | string | new GUID | Pair id; must match both `pair.json` markers. |
| `name` | string | — | Short name used by the CLI. |
| `localRoot` | path | — | Local root folder. |
| `remoteUrl` | URL | — | WebDAV endpoint ([§4.4](04-remote-webdav.md)). |
| `remoteRoot` | string | `/` | Remote root folder under the endpoint. |
| `user` | string | — | Login; the password is in `secrets.dat` (`wds secret set <pair>`); fallback: env `WDS_PASSWORD`. |
| `mode` | `DryRun` \| `ApproveAll` \| `Guarded` \| `Auto` | `ApproveAll` | [§10](10-modes-approval.md). |
| `maxOpsPerCycle` | int? | unlimited | Action ops per cycle ([§10](10-modes-approval.md)). |
| `conflictWinner` | `Local` \| `Remote` \| `Newer` | `Local` | [§6.4](06-sync-algorithm.md). |
| `massDeleteCount` | int | 50 | [§9](09-safety-guards.md) #2–3. |
| `massDeletePercent` | double | 5 | [§9](09-safety-guards.md) #2–3, % of tracked items. |
| `maxCompareDownloadBytes` | long | 64 MB | [§6.2](06-sync-algorithm.md) `Compare`. |
| `minFreeDiskSpace` | long | 2 GB | [§9](09-safety-guards.md) #7. |
| `keepOverwrittenVersions` | bool | true | Old local version to the Recycle Bin before an overwrite. |
| `localDeleteTarget` | `RecycleBin` \| `StateTrash` | `RecycleBin` | [§9](09-safety-guards.md) #5; `StateTrash` = `.webdavsync\trash\<date>\`. |
| `importedBaseMatch` | `SizeOnly` \| `SizeAndMtime` | `SizeOnly` | [§3.5](03-legacy-state.md). |
| `remoteHashProperty` | string? (Clark notation) | from the newest probe report | PROPFIND property holding the content hash ([§4.2](04-remote-webdav.md)); Mail.ru: `{DAV:}getetag`; absent = unknown. CLI: `pair add --remote-hash-property <name\|auto\|none>`. |
| `maxRequestsPerSecond` | double | 5 | [§11](11-bandwidth-pause.md); 0 = unlimited. |
| `maxParallelRequests` | int | 4 | Listings in flight during a remote crawl ([§4.3](04-remote-webdav.md)); 1–16. |
| `setLocalMtimeFromRemote` | bool | true | [§4.6](04-remote-webdav.md). |
| `excludedFolders` | string[] | `[]` | Relative paths (any depth); top-level ones are the selective sync ([§12.2](12-filters.md)). |
| `excludePatterns` | string[] | `[]` | Globs (`*`, `?`); without `/` matched against the name, otherwise against the path ([§12.3](12-filters.md)). |
| `autoApprove` | `{type, folder, overwrite}[]` | `[]` | "Approve and remember" rules ([§10](10-modes-approval.md)); CLI `wds rule`. |

Settings of later milestones (not implemented yet; names reserved): timing ([§8](08-timing.md): `localQuietPeriod`,
`maxBatchDelay`, `fileStableFor`, `remotePollInterval`, `remoteFullCrawlInterval`, `localFullRescanInterval`,
`idleBackoff`), bandwidth and concurrency ([§11](11-bandwidth-pause.md): `uploadLimitKBps`, `downloadLimitKBps`,
`maxParallelTransfers`, schedule), selective sync policy ([§12.2](12-filters.md):
`syncRootFiles`, `newRemoteRootFolders`), `maxFileSize` ([§12.3](12-filters.md)).

CLI: `wds pair show <pair>` prints all settings; `wds pair set <pair> name=value ...` changes them, all or nothing
(`none` clears an optional value; sizes accept `KB`/`MB`/`GB`/`TB`, binary units). Not editable: `id`, `localRoot`,
`remoteUrl`, `remoteRoot` (identity of the pair: base and root markers, [§9](09-safety-guards.md) #1) and, until
exclusion handling ([§12.2](12-filters.md): drop the subtree's base on exclude, union mode on re-include) exists,
`excludedFolders` / `excludePatterns`; `autoApprove` is changed with `wds rule add|remove`.

The data folder can be overridden with `--data-dir` or the environment variable `WDS_DATA_DIR` (used by tests
and for running several setups side by side).
