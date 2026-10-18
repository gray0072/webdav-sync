# 11. Bandwidth, concurrency, pause

- **Bandwidth**: separate upload / download limits (KB/s, 0 = unlimited), token bucket applied to
  the streams. Optional **schedule**: different limits by time of day/day of week (e.g. unlimited at night).
- **Concurrency**: `MaxParallelTransfers` (default 3), `MaxParallelRequests` for metadata (default 4),
  `MaxRequestsPerSecond` (default 5). Large-file transfers count as one slot.
- **Adaptive backoff**: on `429`/`503` halve the request rate, slowly recover.
- **Local load** (implemented): `hashLimitMBps` per pair limits the read rate of hashing (scan, rolling verify,
  §5.3). The tray app's sync work runs at low priority by default (`lowPriority`: the process
  is below normal, the UI thread is raised back to normal, bulk file I/O — hashing, upload reads, download writes —
  gets the very-low I/O priority hint per file handle), so it yields to other programs and runs at full speed when
  the machine is idle, while the tray menu answers at once. Windows background mode (`PROCESS_MODE_BACKGROUND_BEGIN`)
  is not used: it covers the UI thread too and empties the working set, so a right-click waited for the menu code to
  be paged back in at very low disk priority. The process can have a hard CPU
  cap (`cpuLimitPercent`, a job object with CPU rate control: percent of the whole machine). Both app-level settings
  live at the top of `settings.json`; tray: Performance ▸. The CLI is not limited.
- **Pause**: from tray — "Pause for 15 min / 1 h / until tomorrow / until resumed".
  - Pause stops starting new ops immediately; in-flight transfers are cancelled (downloads keep the
    partial temp file for `Range` resume; uploads restart later). Watching continues, so on resume
    the engine knows what changed.
  - Automatic pause (each configurable): metered network, on battery below X %, fullscreen app
    / presentation mode, user-defined schedule windows.
- **Resume**: re-plans from current state (never replays a stale plan blindly).
