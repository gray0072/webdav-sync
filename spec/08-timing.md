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
