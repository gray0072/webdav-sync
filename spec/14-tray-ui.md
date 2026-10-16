# 14. Tray UI

Icon states (with tooltip text):

| State | Icon | Tooltip example |
|---|---|---|
| Idle / up to date | plain | `Up to date · last sync 14:32` |
| Syncing | animated | `Syncing 12 of 40 · ↑ 1.2 MB/s ↓ 3.4 MB/s` |
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
