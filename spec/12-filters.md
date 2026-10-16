# 12. Filters and selective sync

## 12.1 Built-in ignore list

Not configurable off:
`.webdavsync\` (sync state, [§15.2](15-config-storage.md)), `.cloud.db`, `.cloud.db-journal`, `.cloud_ss`, `desktop.ini`, `Thumbs.db`,
`~$*` (Office lock files), `*.tmp` while the file is being written, `$RECYCLE.BIN`, `System Volume Information`.

## 12.2 Root folder selection (selective sync)

The user chooses which **top-level folders** of the root are synced. The selection can be
changed at any time while the app is running ("on the fly"): no restart, no full re-sync.

- Selection is per pair, by top-level folder. Files directly in the root are always synced
  (setting `SyncRootFiles`, default on).
- The list shows the union of top-level folders on both sides (last remote root listing +
  local root), with state (synced / excluded / remote only / local only) and size/count when known.
- **New top-level folders** appearing on the remote: `NewRemoteRootFolders = Include | Exclude | Ask`
  (default `Include`; `Ask` shows it in the tray as "needs attention"). New local top-level
  folders are always included (the user put them there), unless the name matches an excluded
  remote folder → notify and ask, never merge silently.
- Initial selection: all folders included; on legacy import — from `FOLDERS_RECORDS.ignore` ([§3.4](03-legacy-state.md)).
- Storage: in `data\settings.json` with the pair; a snapshot is kept in `state.db` `meta` to detect
  selection changes made while the engine was not running.

Applying a change (takes effect immediately: the current cycle is cut short and a new one is scheduled):

- **Include a folder** → that subtree is synced in **union mode** (like the initial sync, [§7.6](07-scenarios.md)):
  no deletes, identical files (by size + content hash) are just recorded in base, differences become conflicts.
  So re-including a folder whose local copy was kept costs only hashing, not transfers.
- **Exclude a folder** → in-flight and pending ops in that subtree are cancelled; base entries of the
  subtree are dropped; nothing is ever deleted on the remote. For the local copy the user picks:
  - **Keep local copy** (default) — files stay on disk, no longer synced.
  - **Remove local copy** — only items unchanged vs base go to the Recycle Bin; items with
    unsynced local changes are uploaded first (or kept, if the upload fails), and the user sees a summary.
    This is a destructive action → explicit confirmation with counts and sizes, in every mode.
- **Excluded folders are skipped by the remote crawl** (saves requests); the root itself is still listed
  to detect new/renamed/deleted top-level folders.
- Rename of a top-level folder (detected as a move, [§6.3](06-sync-algorithm.md)) keeps its selection state.
  An excluded remote folder deleted remotely disappears from the list.

## 12.3 User filters

Glob patterns (include/exclude), max file size. Excluded items are neither uploaded nor deleted on the other side.

## 12.4 Not synced, reported once as warnings

Symlinks/junctions (not followed), offline/placeholder files from other providers, files with
names invalid on the other side ([§13](13-edge-cases.md)).
