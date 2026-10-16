# 9. Safety guards

1. **Root sanity**: the local root must exist and contain `.webdavsync\pair.json` with the
   pair id from settings. Missing/empty root (unplugged drive, wrong mount) → pair stops with
   an error, nothing is deleted on the remote. Volume serial differs from setup → ask the user
   to confirm the move ([§15.2](15-config-storage.md)); prototype: FileIds are ignored for that cycle (no local
   move detection) and a note is shown. Same for the remote root (must exist and contain
   `/.webdavsync/pair.json` with the same pair id). The remote marker (and, for a pair without a baseline, the
   remote root itself) is created by the first executed cycle; from then on (meta `remote_marker_created`) a
   missing marker or root stops the pair. A pair with a baseline (initial sync done or legacy import) never
   creates a missing remote root.
2. **Mass-delete guard**: if a cycle plans deletes > `MassDeleteCount` (default 50) or
   > `MassDeletePercent` (default 5 %) of tracked items (base entries) on either side → stop and require
   approval, in any mode including Auto. A collapsed folder delete counts every base item of its subtree.
   Note: with the percentage rule a small pair trips on almost any delete (1 of 3 items = 33 %); acceptable for
   the first runs, revisit (e.g. apply the percentage only above a minimum count) before `Auto` is used.
3. **Mass-overwrite guard**: same thresholds for overwrites (`Upload`/`Download` replacing an existing item).
4. **No delete without base**: an item is deleted on side X only if it is unchanged vs base on X; recursive
   deletes re-verify the whole subtree right before executing ([§6.6](06-sync-algorithm.md)).
5. **Recycle Bin / trash**: local deletes and overwrites go to the Recycle Bin; remote deletes
   rely on the cloud trash (verified by probe/setting). If the Recycle Bin is unavailable for a
   path (e.g. too large, or a path ≥ 260 chars) → move to `<local root>\.webdavsync\trash\<date>\...` instead of hard delete.
6. **Quota check**: before uploads, compare total upload size with available quota (when the server reports it)
   → error, the cycle does not run.
7. **Disk space check**: before downloads, keep `MinFreeDiskSpace` (default 2 GB) free → error, the cycle does not run.
8. **Clock sanity**: never decide by mtime ordering alone; hashes and base decide.
