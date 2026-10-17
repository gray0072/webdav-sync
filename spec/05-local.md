# 5. Local model

- Enumerate with `FindFirstFileEx`-style APIs (via `System.IO.Enumeration`) using long paths (`\\?\`).
- Per item: relative path, name, FileId (`GetFileInformationByHandle` / `FILE_ID_INFO`),
  size, mtime (UTC), attributes, isDir.
- `FileSystemWatcher` on the root (recursive, large buffer, 64 KB). On buffer overflow or
  watcher error → schedule a full local rescan.
- Periodic full local rescan as a safety net (default every 6 h, and on startup, resume from sleep).
- A local file is **changed** if size or mtime differs from base; then content hash is computed to
  confirm (an mtime-only touch with the same hash just updates the base).
- Changes made while the app is not running are found the same way: every cycle (the first one at start too) scans
  the whole tree and compares size and mtime with the base.

## 5.3 Content changes that keep size and mtime; rolling verify

- When a file's hash is known although size and mtime equal the base (the file was hashed again: a new FileId, or
  the rolling verify) and differs from the base's local hash, the file is **modified**. Such an op gets
  `NeedsReview`: it waits for approval in every mode, rules included (§10), because the same picture is produced by
  silent disk corruption, and uploading a corrupted file over the good cloud copy must be a human decision. Reason
  text: "…; content differs, size and time unchanged".
- Rolling verify (`localVerifyInterval`, off by default; tray: Settings ▸ Verify local files): every synced file is
  hashed again once per interval, a slice per cycle, so there is never one long scan (415 GB at 50 MB/s would take
  2.5 h). Slice budget = synced bytes × (time since the last slice / interval), at most 4 GB; files are taken in key
  order after the cursor (`meta.local_verify_cursor`), wrapping around; a file larger than 4 GB is taken alone once
  the budget reaches 4 GB. The first cycle with the setting on only starts the clock (`meta.local_verify_at`).
- Hashing reads are limited by `hashLimitMBps` (0 = unlimited; §11).
- **Stability check** before upload: file must be unchanged (size, mtime) for the quiet period,
  openable with `FileShare.Read` (not exclusively locked), and not change while being read
  (re-check size/mtime after the upload; if changed → re-queue).
