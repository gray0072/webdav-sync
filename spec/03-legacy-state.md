# 3. Legacy client state (`.cloud.db`, `.cloud_ss`)

Findings from inspecting the real files (2026-10-16). All of this is reverse-engineered;
treat it as best-effort and validate during import.

## 3.1 `.cloud.db` — SQLite 3 (written by SQLite 3.9.1), `db_version = 0518`

| Table | Meaning |
|---|---|
| `GENERAL_INFO(attribute, value)` | KV: `db_version`, `client_version`, `email`, `r_size_free`, `r_size_total` (quota bytes), `r_root_id`, `l_root_id`. |
| `TREE_REV_INFO(id, tree_id, rev_new, rev_old, rev_local, access, tree_type)` | One row per tree. `tree_id` is hex of an ASCII id; `rev_*` are remote tree revisions of the native API. Row 1 = local tree (`tree_type` 3), row 2 = remote tree (`tree_type` 0). |
| `FOLDERS_RECORDS(id, inode, key, name, mapping_name, changed, ignore, rev_info_id)` | Folders of both trees; `rev_info_id` says which tree. Root folders are named `/` and their ids are `l_root_id` / `r_root_id`. `ignore` = selective-sync exclusion. |
| `FILES_RECORDS(id, inode, key, name, mapping_name, changed, needs_update, time, size, hash)` | Files of both trees (ids are a separate namespace from folders). |
| `CLOSURE_TABLE(parent_id, child_id, child_is_folder)` | Despite the name, it is a **parent → direct child adjacency list**. `child_is_folder` tells whether `child_id` refers to `FOLDERS_RECORDS` or `FILES_RECORDS`. |

Column semantics:

- `key` — `name` lower-cased (Unicode, incl. Cyrillic). Used for case-insensitive matching.
- `name` — real name with case.
- `mapping_name` — usually empty; when set it equals the name (seen on 6 rows). Probably a
  local↔remote name mapping for names not representable on one side. Import: if non-empty, prefer it as the local name.
- `inode` — NTFS 64-bit File Reference Number of the local file: exactly the file index of
  `GetFileInformationByHandle` (MFT record number + 16-bit sequence number in the high bits). Confirmed on the real
  folder: equal for 43 698 of 43 698 unchanged files. The remote tree carries the same value copied from the local
  item. Imported as the base `LocalFileId`, so local moves are detected from the first cycle.
- `time` — mtime in Unix seconds. Local and remote trees may differ for the same file
  (remote keeps the upload time / original time).
- `size` — bytes. `hash` — content hash, 40 uppercase hex chars (§3.3).
- `changed`, `needs_update` — pending-work flags; all `0` in a clean state.

Observed data: ~43.9k files and 893 folders per tree (plus the root); local and remote trees differ by a
handful of items (the old client's last unfinished work). The disk currently has fewer files
than the DB local tree, i.e. the user changed the folder after the old client stopped
(2026-10-16: 184 files missing on disk, 3 changed, none new).

## 3.2 `.cloud_ss`

Zero-length file with the same mtime as `.cloud.db`. Probably a "safe shutdown"/session
sentinel. No content to use. v1 ignores it (never syncs, never deletes it).

## 3.3 Content hash (verified on real files)

- `size < 21`: the file bytes, zero-padded to 20 bytes, as uppercase hex (40 chars).
  Empty file → 40 zeros.
- `size ≥ 21`: `SHA1("mrCloud" ‖ content ‖ ASCII(decimal size))`, uppercase hex.

This is the canonical content hash of the engine for local files (and remote files when the
remote exposes it). Implemented as a streaming hasher; computed lazily and cached in state
by (FileId, size, mtime). The implementation matches `.cloud.db` on real files (`wds scan D:\Cloud --hash None --sample 300`: 300 of 300 equal).

## 3.4 How we use it

- **Import (read-only)**: on pair creation (`wds pair add --import-legacy`, or later `wds import <root> --pair <p>`),
  if `<root>\.cloud.db` exists, it can be used as the baseline. The importer opens it **read-only**
  (copy first, then open the copy), reconstructs both trees, and builds **Base**:
  - folder present in both trees → base entry.
  - file present in both trees with equal `hash` and `size` → base entry (synced): local side from the local
    tree (`inode`, `size`, `time`, `hash`, name/`mapping_name`), remote side from the remote tree; no etag;
    `RemoteConfirmed = false` (§3.5).
  - item present in only one tree or with different hashes → no base entry (it will be
    treated as a fresh two-sided create and resolved by the rules of [§6](06-sync-algorithm.md)).
  - folders with `ignore = 1` → added to `ExcludedFolders` (selective sync).
- The imported baseline **counts as a completed initial sync**: the old client did complete cycles, so changes
  made since it stopped — including deletions — are propagated normally, under the safety guards ([§7.4](07-scenarios.md)).
  The remote marker ([§9](09-safety-guards.md) #1) is created by the first cycle.
- The legacy files are never modified, never synced, never deleted. They are in the
  built-in ignore list.
- Writing a compatible `.cloud.db` (so the old client could resume) is **out of scope** for v1 (see [§15](15-config-storage.md)).

## 3.5 Imported base vs the first remote crawl

Imported entries have no etag and the remote `time` may not equal the server's `getlastmodified` ([§18](18-open-questions.md) #6).
Setting `ImportedBaseMatch`:

- `SizeOnly` (default): an unconfirmed entry whose remote size is unchanged counts as touched → `UpdateBase`
  adopts the remote identity (etag, mtime) and sets `RemoteConfirmed`. Risk: a remote edit with the same size made
  after the old client stopped is missed (it would be overwritten only if the file is also edited locally — then
  it is an upload over the remote version).
- `SizeAndMtime`: also requires `getlastmodified` to equal the legacy remote `time` (to the second); otherwise
  the entry counts as remotely modified (download, under the mass-overwrite guard).
- If the server exposes the content hash, it is compared instead of both.

Offline check on the real folder (local scan + imported base + the legacy remote tree as a stand-in for an
unchanged remote): 44 541 `UpdateBase`, 3 `Upload`, 14 `DeleteRemote` covering 232 items (184 files, 48 folders)
→ the mass-delete guard trips, as expected. Re-check with the real crawl once WebDAV access is available.
