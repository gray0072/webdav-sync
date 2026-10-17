# 14. Tray UI

Icon states (with tooltip text):

| State | Icon | Tooltip example |
|---|---|---|
| Idle / up to date | plain | `Up to date · last sync 14:32` |
| Checking (scan / crawl, nothing transferred) | plain | `Checking for changes` |
| Syncing (ops running) | animated | `Syncing 12 of 40 · ↑ 1.2 MB/s ↓ 3.4 MB/s` |
| Waiting (debounce/poll) | plain + dot | `Changes detected, syncing in 20 s` |
| Paused | pause badge | `Paused until 15:00` |
| Needs attention | yellow badge | `3 items need approval · 1 conflict` |
| Error | red badge | `Error: auth failed` |
| Offline | grey | `Offline, retrying in 1 min` |

Context menu: Open folder · Open cloud in browser · Sync now · Pause ▸ (15 min, 1 h, until tomorrow, until resumed) / Resume ·
Approvals (N) · Conflicts (N) · Errors (N) · Activity · Choose folders… · Settings · Mode ▸ (DryRun, ApproveAll, Guarded, Auto) · Exit.

Windows:

- **Activity**: current queue with progress per transfer, recent completed ops (filterable), speeds.
- **Approvals**: grouped plan, checkboxes, Approve selected / Approve all / Reject, details pane.
- **Conflicts**: list with actions ([§6.4](06-sync-algorithm.md)).
- **Errors**: per-item errors with retry / ignore / open log.
- **Choose folders**: checkbox list of top-level folders with state and size; changes apply on the fly
  ([§12.2](12-filters.md)); excluding asks "keep / remove local copy".
- **Settings**: account (URL, login, app password), folders, mode, timing, limits, schedule, filters, startup with Windows.

Notifications (Windows toasts): errors, new conflicts, approvals waiting, mass-delete guard triggered.
Rate-limited (one per category per 10 min), never a toast per file.

## 14.1 Implemented (first tray milestone, `src/WebDavSync.App`, executable `WebDavSync.exe`)

- `WebDavSync.exe [--data-dir <path>]` (default `<app dir>\data` or `WDS_DATA_DIR`); exits with an error if the data
  folder is not writable; one instance per data folder (named mutex). One `PairService` (§8) per pair of
  `settings.json`; the password comes from the Credential Manager, then `WDS_PASSWORD`.
- Icon: all states of the table, drawn at startup; syncing pulses smoothly (24 frames, cosine between two blues,
  ~2 s per pulse); a background check is quiet (Checking looks like Idle), so the periodic crawl does not animate; the most severe pair state wins
  (Error > Needs attention > Offline > Syncing > Paused > Waiting > Idle). Tooltip: status text (+ last sync time).
- Menu (per pair; a submenu per pair when there are several): status, Open folder, Open cloud in browser (Mail.ru),
  Sync now, Pause ▸ (15 min, 1 h, until tomorrow 06:00, until resumed) / Resume, Approvals (N)…, Choose folders…,
  Settings ▸ (Mode, Check cloud every 15 min … 24 h, Verify local files off / 7 / 30 / 90 days, Hashing speed,
  Set password…); then Performance ▸ (Low priority, CPU limit none / 10 / 25 / 50 %), Start with Windows (HKCU `Run`,
  with the data folder), Open log folder, Exit. Left click opens the
  approvals of a pair that has some, otherwise the folder. Mode changes re-read `settings.json` before saving.
- Approvals window: pending ops with checkboxes (red: deletes, overwrites, conflicts), Approve checked / Approve all
  (with a confirmation that counts deletions; a guard trip lists the trips), Reject checked (hold), Check all.
- Notifications: balloon tips for approvals waiting, failed ops and errors, one per pair and category per 10 min.
- Choose folders: checkbox list of the top-level folders (local + cloud / local only / cloud only); unchecking
  asks for confirmation and states that nothing is deleted (§12.2).
- Not yet: Activity, Conflicts and Errors windows, Settings window, Windows toasts.
