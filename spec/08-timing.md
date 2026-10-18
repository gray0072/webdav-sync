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
retry after 1, 2, 4 … 15 min; anything else → `Error`, retry at the poll interval. Full or incremental: the first cycle, "sync now", retries and the poll crawl the remote fully; cycles caused by
local changes (and the follow-up after a `maxOpsPerCycle` cut) re-list only the parents of the changed paths and the
folders our own ops touched (§4.3 #3), as long as the cached snapshot is complete and the poll interval since the last
full crawl has not passed. The poll is counted from the last full crawl, so steady local activity cannot postpone
it. A watcher overflow or a failed cycle drops the cache.

Remote change check (a store with a change token, i.e. the Mail.ru API, [§4.7](04-remote-webdav.md)): every
`remoteCheckInterval` (default 1 min; 0 = off) one small request compares the remote root's change token with the
one recorded by the last complete full crawl; a difference starts a cycle with a full crawl ("remote change"), long
before the poll. The cycle after one that changed the remote is followed by one more (cheap) cycle, because our own
ops change the token too. No checks while paused, while a retry is pending or without a recorded token (the API is not
available, or the last full crawl was incomplete); the poll still runs.

Start-up trust: `state.db` `meta.last_full_crawl` records every complete full crawl (CLI or tray). If it is younger
than `remotePollInterval` when the service starts (and the initial sync is done), the first cycle builds the remote
snapshot from the base (each synced item's remote path, size, mtime, etag, hash; every base folder counts as listed)
and re-lists only the root. Local changes made while the app was closed are found by the local scan; the next full
crawl is due one poll interval after the recorded one. Held items are released only against a full crawl (a cached
or base-made snapshot can miss the remote-only items whose ops are held). Not implemented yet: `fileStableFor`,
`localFullRescanInterval`, `idleBackoff`.
