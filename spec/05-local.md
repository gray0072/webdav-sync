# 5. Local model

- Enumerate with `FindFirstFileEx`-style APIs (via `System.IO.Enumeration`) using long paths (`\\?\`).
- Per item: relative path, name, FileId (`GetFileInformationByHandle` / `FILE_ID_INFO`),
  size, mtime (UTC), attributes, isDir.
- `FileSystemWatcher` on the root (recursive, large buffer, 64 KB). On buffer overflow or
  watcher error → schedule a full local rescan.
- Periodic full local rescan as a safety net (default every 6 h, and on startup, resume from sleep).
- A local file is **changed** if size or mtime differs from base; then content hash is computed to
  confirm (an mtime-only touch with the same hash just updates the base).
- **Stability check** before upload: file must be unchanged (size, mtime) for the quiet period,
  openable with `FileShare.Read` (not exclusively locked), and not change while being read
  (re-check size/mtime after the upload; if changed → re-queue).
