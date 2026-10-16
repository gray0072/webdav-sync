# 6. Sync algorithm

## 6.1 Cycle

1. Collect dirty set: local paths from watcher events / rescans, remote paths from crawls.
   (Prototype: always a full remote crawl and a full local scan.)
2. Wait for the **quiet period** ([§8](08-timing.md)), then snapshot L and R for the dirty set (plus their parents/children as needed).
3. **Plan**: three-way compare L, R, B per key (§6.2), detect moves (§6.3), order ops (§6.5). Contract: §6.9.
4. **Safety guards** ([§9](09-safety-guards.md)) — may stop the cycle and require approval.
5. **Approval gate** ([§10](10-modes-approval.md)).
6. **Execute** ops through the throttled executor; each op is journaled before and after.
7. **Commit**: on each successful op, update base for that item in the same transaction as the journal record.

## 6.2 Decision table (per key, files)

`=` same as base, `≈` touched (metadata differs, content same), `Δ` modified, `+` created (no base),
`−` deleted, `∅` absent (no base). How each side is classified: §6.9. Test ids (`F1`…) are unit test names.

| L \ R | `=` / `≈` | `Δ` | `−` | `∅`/`+` |
|---|---|---|---|---|
| `=` / `≈` | nothing (F1); UpdateBase if either side is `≈` or paths changed (Edge13) | Download (F2) | DeleteLocal (to Recycle Bin) (F3) | — |
| `Δ` | Upload (F4) | equal hash → UpdateBase (F6); different size → **Conflict** (F5); same size, no remote hash → **Compare** (F7) | **Upload** (edit wins over delete), notify (F8) | — |
| `−` | DeleteRemote (to cloud trash) (F9) | **Download** (edit wins over delete), notify (F10) | ForgetBase (F11) | — |
| `+` | — | — | — | R absent → Upload (F12); R `+`: same as `Δ`/`Δ` (F14) |
| `∅` | — | — | — | R `+` → Download (F13) |

Either side unknown (§6.8: not known absent, or a changed file that could not be read) → no op for the key.

`Compare` (remote has no hash, sizes equal): download to temp and compare the content hash, bounded by
`MaxCompareDownloadBytes` (default 64 MB; above that → Conflict, keep both). Equal → UpdateBase; different →
conflict resolution reusing the downloaded temp file.

In **union mode** (initial sync, §7.6, and union subtrees below) deletes are replaced by restores:
`=`/`−` → Upload, `−`/`=` → Download; `−`/`−` still forgets the base.

### Folders (D1–D9)

Folders are decided bottom-up, after their children. "Children exist on X" means: after the plan, some child
exists on side X (kept, created, or not synced there — skipped/unknown items count as existing).

| Id | Local | Remote | Base | Decision |
|---|---|---|---|---|
| D1 | present | absent | none | MkdirRemote (before the children's uploads) |
| D2 | absent | present | none | MkdirLocal |
| D3 | present | present | none / paths changed | UpdateBase (empty folders are synced, §13 #20) |
| D4 | absent (deleted) | present | yes | no children exist locally and none remotely that must stay → one DeleteRemote for the whole subtree (collapsed) |
| D5 | absent (deleted) | present | yes | a remote child is new/changed → MkdirLocal (re-create) + Download of it; unchanged children are deleted individually; notify |
| D6 | present | absent (deleted) | yes | mirror of D4/D5: DeleteLocal, or MkdirRemote + Upload of the changed children |
| D7 | absent | absent | yes | one ForgetBase for the subtree |
| D8 | file | folder | any | file vs folder: **Conflict** — the file side's file is renamed to the conflict name and copied to the other side; the key becomes a union subtree (no deletes), so the folder is re-created on the file side with its content |
| D9 | folder | file (base: file) | yes | same as D8 (type change vs base never deletes, §13 #18) |

A folder that would be deleted on side X but still has children on X that are not synced there (invalid names,
user-excluded, reparse points, unreadable) is **kept** on X (never a recursive delete) and the user is warned.
A key whose present type differs from its base type also becomes a union subtree.

## 6.3 Moves and renames (M1–M10)

Moves are detected before the decision pass and simulated on the working state (phase 0, §6.5), so a move
followed by a modification becomes Move + Upload/Download.

**Local moves** (`MoveRemote`): a base item whose key is known absent locally (§6.8) and a new local item
(no base) with the same **FileId** and the same type, not inside the old item. A FileId alone is weak evidence
(MFT records of deleted files are reused; ids are weaker on other file systems), so it must be confirmed:

- file: same size and mtime as in base (pure move/rename, M1), or the same name (moved and modified, M3);
- folder: same name, or at least half of the base's descendant files found under the new folder with the same
  relative key and size (M2, §13 #16: one MOVE for the whole folder).

Several candidates (hard links, §13 #21): prefer the same name, then the same parent; still ambiguous → no move.
The remote source must still exist with the same type and the remote target must be free and known absent;
otherwise fall back to delete + create (M4). A missing remote parent of the target is created first by an early
MkdirRemote (M5).

**Remote moves** (`MoveLocal`): a base item whose key is known absent remotely and that still exists locally,
and a new remote item (no base, absent locally) of the same type with the **same name** (tier 1) or the
**same parent** (tier 2):

- file: same size and (same hash if both known, else same etag or same mtime as in base) (M6, M7);
- folder: score = share of the base's descendant files found under the candidate with the same relative key
  and size; at least 0.5 (M10).

The best tier wins, then the best score; a tie in the best tier → ambiguous, no move, warning (M8, §13 #15).
Same-name candidates beat same-parent ones (M9). The server may assign new etags on MOVE: within a remote-moved
subtree, a file with the same size and mtime counts as touched (UpdateBase adopts the new etag), not modified.

**Case-only rename** (`a.txt` → `A.txt`, Edge1): key unchanged, the name differs from the base name of the same
side → rename on the other side via a temp name. A difference only in Unicode normalization is not a rename (§13 #5).

- **Content reuse** (download avoided by moving a local file with the same hash): not in the prototype.
- If move target conflicts with an existing item → no move; the decision table applies to both keys.
- A move out of the sync root looks like a delete; a move into it looks like a create.

## 6.4 Conflicts

- Default resolution: **keep both**. The losing version is renamed
  `name (conflict YYYY-MM-DD HHmmss <side>).ext`, where `<side>` is the machine name or `cloud`.
  The local version keeps the original name by default (setting: `ConflictWinner = Local|Remote|Newer`).
  If the name is taken, ` 2`, ` 3`… is appended inside the parentheses.
- Execution (loser = remote): download the remote version to temp, MOVE it on the server to the conflict name,
  move the temp to the local conflict name, upload the local version to the original name.
  Loser = local: rename locally, upload the copy, download the remote version to the original name.
- The conflict copy is synced like any other file (so both versions exist on both sides).
- Every conflict is recorded in the state DB and shown in the Conflicts window until dismissed.
- Conflicts window actions: open both, keep local, keep remote, keep both (dismiss).

## 6.5 Operation ordering

0. **Phase 0 (early)**, in detection order: file-vs-folder conflict renames, moves/renames (shallow before
   deep), and MKCOL of missing parents of move targets. Later ops were planned on the state after these, so if
   one of them is not approved or does not complete, the rest of the plan is not executed (re-planned next cycle).
1. Folder creates (top-down).
2. Base-only updates.
3. Uploads/downloads/compares/conflicts (small files first, then large; configurable priority later).
4. Deletes (bottom-up), last — so an interrupted cycle never leaves data deleted but not yet copied.
5. ForgetBase.

## 6.6 Transfers

- **Download**: to `<local root>\.webdavsync\tmp\<op id>.part` (same volume) → verify size (and content hash if
  remote provides it, and the response etag) → set mtime (§4.6) → re-check that the local target did not change
  → old version to the Recycle Bin (`KeepOverwrittenVersions`) → atomic `File.Move(overwrite)`.
  A local change in the meantime → op skipped, re-planned (becomes a conflict next cycle).
  Resume interrupted downloads with `Range`: not in the prototype.
- **Upload**: stream the file with `FileShare.ReadWrite | Delete`, hashing while reading; after `PUT`, PROPFIND
  the target to verify size (and hash if exposed); if local changed during upload → op skipped, re-queued.
  `PUT` sends `X-OC-Mtime` (§4.6).
- **Preconditions**: before every op the executor re-checks the planned state of both sides (local size+mtime,
  remote etag or size+mtime). A recursive delete re-verifies the whole subtree (local enumeration / remote
  re-crawl) against the base. Any difference → op skipped, re-planned next cycle.
- Overwrite on either side goes through the same "safe overwrite": the old version is moved to
  the Recycle Bin / cloud trash (or the remote's own versioning) when the setting
  `KeepOverwrittenVersions` is on (default on locally).
- Retries: per [§4.5](04-remote-webdav.md).

## 6.7 Crash safety

- Journal: each op is recorded `running → done/failed/skipped` in SQLite (WAL mode); base-only ops are applied
  in batches without journal rows.
- On start, ops left `running` are marked `interrupted`; the next cycle re-plans from the actual L/R state.
  This is safe because the base is updated only after an op is verified done: an interrupted upload or download
  shows up as a change (or a conflict) and is redone. Re-validating individual journal entries (e.g. "upload is
  done if the remote already has the expected size/hash") is a later optimization.
- Temp files in `tmp\` are deleted on start.

## 6.8 Complete listing and "known absent" (invariant #1)

A remote folder listing is **complete** when: `PROPFIND Depth: 1` returned `207` with an XML body
(`text/html` → offline, captive portal), the body parsed, the folder itself is in the response, every response
is a direct child of the folder, and every response has a `propstat` with status 200 that includes
`resourcetype`. A folder that returns 404 during the crawl makes its parent's listing incomplete (it is stale).
Transient errors after retries → the folder is not listed; auth and offline errors abort the crawl.
A local folder is listed when its enumeration finished without an exception.

An item missing from a snapshot is **known absent** only if its nearest existing ancestor is a completely listed
folder (or a file). Otherwise its state is unknown and nothing is planned for it. A cycle is "full" (§7.6) only
when both snapshots are complete.

## 6.9 Domain model and planner contract

```
LocalEntry  (Key, Path, IsDir, Size, MtimeUtc, FileId?, Hash?, Locked)
RemoteEntry (Key, Path, IsDir, Size, MtimeUtc?, ETag?, Hash?)
BaseEntry   (Key, IsDir, LocalPath, RemotePath, LocalFileId?, LocalSize, LocalMtimeUtc?, LocalHash?,
             RemoteETag?, RemoteSize, RemoteMtimeUtc?, RemoteHash?, RemoteConfirmed, SyncedAtUtc)
Snapshot    (Entries by key, ListedFolders, SkippedKeys, Warnings)
PlannerInput(LocalSnapshot, RemoteSnapshot, Base by key, PlannerSettings)
PlannerSettings(UnionMode, ConflictWinner, Now, MachineName, TrustLocalFileIds, ImportedBaseMatch,
                MaxCompareDownloadBytes, Filter)
SyncOp      (Type, Key, IsDir, Reason, LocalPath, RemotePath, NewKey, NewLocalPath, NewRemotePath,
             Local, Remote, Base  -- the states the planner saw, re-checked by the executor --
             Overwrite, Conflict kind, Loser side, Affected, Early)
Plan        (Ops in execution order, Warnings)
```

- Paths are relative, `/`-separated; the key is `NFC(path).ToLowerInvariant()`. Base keeps both sides' paths
  because names can differ in case or normalization.
- `SkippedKeys`: items that exist on a side but are not synced (§12.4); they pin their ancestors (§6.2 folders).
- `Now` and `MachineName` are inputs, so the planner is deterministic.
- Local classification vs base: size+mtime equal → `=`; else unreadable → unknown; else hash equal → `≈`; else `Δ`.
- Remote classification vs base: size differs → `Δ`; both hashes known → equal: `=` or `≈`, else `Δ`; base not
  confirmed (legacy import, §3.5) → `≈` or `Δ` per `ImportedBaseMatch`; else same identity (§4.3) → `=`, else `Δ`.
- Op reasons are short human-readable strings shown in the plan ("local modified", "remote deleted", …).
