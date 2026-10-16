# 11. Bandwidth, concurrency, pause

- **Bandwidth**: separate upload / download limits (KB/s, 0 = unlimited), token bucket applied to
  the streams. Optional **schedule**: different limits by time of day/day of week (e.g. unlimited at night).
- **Concurrency**: `MaxParallelTransfers` (default 3), `MaxParallelRequests` for metadata (default 4),
  `MaxRequestsPerSecond` (default 5). Large-file transfers count as one slot.
- **Adaptive backoff**: on `429`/`503` halve the request rate, slowly recover.
- **Pause**: from tray — "Pause for 15 min / 1 h / until tomorrow / until resumed".
  - Pause stops starting new ops immediately; in-flight transfers are cancelled (downloads keep the
    partial temp file for `Range` resume; uploads restart later). Watching continues, so on resume
    the engine knows what changed.
  - Automatic pause (each configurable): metered network, on battery below X %, fullscreen app
    / presentation mode, user-defined schedule windows.
- **Resume**: re-plans from current state (never replays a stale plan blindly).
