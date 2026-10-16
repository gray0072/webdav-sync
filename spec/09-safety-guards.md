# 9. Safety guards

1. **Root sanity**: the local root must exist and contain `.webdavsync\pair.json` with the
   pair id from settings. Missing/empty root (unplugged drive, wrong mount) → pair stops with
   an error, nothing is deleted on the remote. Volume serial differs from setup → ask the user
   to confirm the move ([§15.2](15-config-storage.md)). Same for the remote root (must exist and
   contain `/.webdavsync/pair.json` with the same pair id).
2. **Mass-delete guard**: if a cycle plans deletes > `MassDeleteCount` (default 50) or
   > `MassDeletePercent` (default 5 %) of tracked items on either side → stop and require
   approval, in any mode including Auto.
3. **Mass-overwrite guard**: same thresholds for overwrites.
4. **No delete without base**: an item is deleted on side X only if it is unchanged vs base on X.
5. **Recycle Bin / trash**: local deletes and overwrites go to the Recycle Bin; remote deletes
   rely on the cloud trash (verified by probe/setting). If the Recycle Bin is unavailable for a
   path (e.g. too large) → move to `<local root>\.webdavsync\trash\<date>\...` instead of hard delete.
6. **Quota check**: before uploads, compare total upload size with available quota.
7. **Disk space check**: before downloads, keep `MinFreeDiskSpace` (default 2 GB) free.
8. **Clock sanity**: never decide by mtime ordering alone; hashes and base decide.
