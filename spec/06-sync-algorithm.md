# 6. Sync algorithm

## 6.1 Cycle

1. Collect dirty set: local paths from watcher events / rescans, remote paths from crawls.
2. Wait for the **quiet period** ([§8](08-timing.md)), then snapshot L and R for the dirty set (plus their parents/children as needed).
3. **Plan**: three-way compare L, R, B per key (§6.2), detect moves (§6.3), order ops (§6.5).
4. **Safety guards** ([§9](09-safety-guards.md)) — may stop the cycle and require approval.
5. **Approval gate** ([§10](10-modes-approval.md)).
6. **Execute** ops through the throttled executor; each op is journaled before and after.
7. **Commit**: on each successful op, update base for that item in the same transaction as the journal record.

## 6.2 Decision table (per key, files)

`=` same as base, `Δ` modified, `+` created (no base), `−` deleted, `∅` absent (no base).

| L \ R | `=` | `Δ` | `−` | `∅`/`+` |
|---|---|---|---|---|
| `=` | nothing | Download | DeleteLocal (to Recycle Bin) | — |
| `Δ` | Upload | equal hash → UpdateBase; else **Conflict** | **Upload** (edit wins over delete), notify | — |
| `−` | DeleteRemote (to cloud trash) | **Download** (edit wins over delete), notify | remove base | — |
| `+` | — | — | — | R absent → Upload; R `+` with equal size & hash → UpdateBase; else **Conflict** |
| `∅` | — | — | — | R `+` → Download |

Remote "equal hash" when the remote has no hash: if sizes differ → different; if sizes equal
and no hash available → download to temp and compare content hash (bounded by setting
`MaxCompareDownloadBytes`, default 64 MB; above that → Conflict, keep both).

Folders:

- Create on one side → create on the other.
- Delete on one side → delete on the other **only the items that are unchanged vs base**;
  anything new or modified inside survives, the folder is re-created to hold it, and the user is notified.
- Folder vs file with the same key → Conflict (rename the newer side).

## 6.3 Moves and renames

- **Local**: an item that disappeared at path A while an item with the same FileId appeared
  at path B is a move. Folder moves become one `MoveRemote` (server-side), not N uploads.
- **Remote**: a deleted+created pair with the same (size, etag/hash or mtime) and the same name
  (or same parent) is treated as a move → `MoveLocal`.
- **Content reuse**: before downloading a file, if a local file scheduled for deletion has the
  same content hash, move it locally instead of downloading.
- Case-only rename (`a.txt` → `A.txt`): explicit rename op on the other side (via temp name if the side is case-insensitive).
- If move target conflicts with an existing item → Conflict rules.
- Move + modify of the same item → Move, then Upload/Download.
- A move out of the sync root looks like a delete; a move into it looks like a create.

## 6.4 Conflicts

- Default resolution: **keep both**. The losing version is renamed
  `name (conflict YYYY-MM-DD HHmmss <side>).ext`, where `<side>` is the machine name or `cloud`.
  The local version keeps the original name by default (setting: `ConflictWinner = Local|Remote|Newer`).
- The conflict copy is synced like any other file (so both versions exist on both sides).
- Every conflict is recorded in the state DB and shown in the Conflicts window until dismissed.
- Conflicts window actions: open both, keep local, keep remote, keep both (dismiss).

## 6.5 Operation ordering

1. Moves/renames (folders before files, shallow before deep).
2. Folder creates (top-down).
3. Uploads/downloads (small files first, then large; configurable priority).
4. Deletes (bottom-up), last — so an interrupted cycle never leaves data deleted but not yet copied.

## 6.6 Transfers

- **Download**: to `<local root>\.webdavsync\tmp\<op id>.part` (same volume) → verify size (and content hash if
  remote provides it) → set mtime → atomic `File.Move(overwrite)`; if the target was modified
  locally in the meantime → Conflict instead of overwrite. Resume interrupted downloads with `Range`.
- **Upload**: stream the file with `FileShare.ReadWrite | Delete`; after `PUT`, PROPFIND the
  target to verify size; if local changed during upload → re-queue. If the server supports
  setting mtime → preserve local mtime.
- Overwrite on either side goes through the same "safe overwrite": the old version is moved to
  the Recycle Bin / cloud trash (or the remote's own versioning) when the setting
  `KeepOverwrittenVersions` is on (default on locally).
- Retries: exponential backoff with jitter (1 s → 5 min), honor `Retry-After`;
  permanent errors (4xx except 408/423/429) go to the error list without infinite retries.

## 6.7 Crash safety

- Journal: each op is recorded `pending → running → done/failed` in SQLite (WAL mode).
- On start, `running` ops are re-validated against actual L/R state (ops are idempotent:
  e.g. upload is "done" if remote already has the expected size/hash).
- Base is updated only after an op is verified done.
