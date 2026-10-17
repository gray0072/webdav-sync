# 8. Timing — "optimal, not instant"

| Setting | Default | Meaning |
|---|---|---|
| `LocalQuietPeriod` | 5 s | Wait this long after the last local event in a burst before planning. |
| `MaxBatchDelay` | 60 s | Upper bound: plan at most this long after the first event of a burst, even if events keep coming. |
| `FileStableFor` | 5 s | A file must be unchanged this long before upload (large files: until writes stop). |
| `RemotePollInterval` | 2 min | Light remote check (incremental crawl or watched folders). Adaptive: 1 min after recent activity, up to 15 min when idle. |
| `RemoteFullCrawlInterval` | 15 min | Full remote crawl. |
| `LocalFullRescanInterval` | 6 h | Safety-net local rescan. |
| `IdleBackoff` | on | Slow polling when nothing changed for a long time, when on battery, or on a metered network. |

Ignore our own echoes: changes produced by our own ops are tagged and filtered out of the next dirty set.

Implemented (`PairService`, Core): `localQuietPeriod`, `maxBatchDelay`, `remotePollInterval` (default 15 min: every
cycle is a full crawl until the incremental crawl exists, §4.3). One loop per pair runs cycles, approvals and
rejections one at a time. Triggers: the first cycle on start, local changes (`FileSystemWatcher` on the root,
recursive; events under `.webdavsync\` and built-in ignored names dropped; a buffer overflow schedules a cycle,
which rescans everything), the poll interval, "sync now" and the end of a pause. Echoes: the local paths (and their
folders) of the ops about to run are ignored until 10 s after the cycle. If `maxOpsPerCycle` cut runnable ops, the
next cycle starts right away. Errors: auth → `Error`, no retry until "sync now"; offline / transient → `Offline`,
retry after 1, 2, 4 … 15 min; anything else → `Error`, retry at the poll interval. Not implemented yet:
`fileStableFor`, `localFullRescanInterval`, `idleBackoff`.
