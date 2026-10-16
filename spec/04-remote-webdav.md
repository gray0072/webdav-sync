# 4. Remote (WebDAV) model

## 4.1 `IRemoteStore`

```
ListAsync(path, depth)            -> entries (name, isDir, size, mtime, etag?, hash?, contentType?)
GetAsync(path, range?)            -> stream
PutAsync(path, stream, length, mtime?)
MkdirAsync(path)
MoveAsync(from, to, overwrite=false)
DeleteAsync(path)
GetQuotaAsync()                   -> used/available (RFC 4331) if supported
ProbeAsync()                      -> RemoteCapabilities
```

## 4.2 Capability probe (run on pair setup and from CLI `probe`)

Detect and persist in state:

- Auth OK (Basic auth with an **app password** issued by the provider for WebDAV).
- `PROPFIND Depth: 1` works; whether `Depth: infinity` is allowed.
- Which properties are returned: `getetag`, `getlastmodified`, `getcontentlength`,
  quota props, any hash-like custom property.
- Whether a folder's `getetag`/`getlastmodified` changes when a descendant changes
  (enables incremental remote crawl).
- Whether mtime can be set (`PROPPATCH` `Win32LastModifiedTime` / `getlastmodified`) and is preserved by `PUT`.
- `MOVE` of a folder is server-side and atomic.
- `DELETE` goes to the cloud trash (verify via web UI manually; record as a setting).
- Case sensitivity: create `Probe.txt`, `PROPFIND probe.txt`.
- Maximum upload size, behavior on quota exceeded (expect `507`).
- Rate-limit behavior (`429`, `503`, `Retry-After`).

All probe actions happen inside a dedicated remote folder `/.webdavsync-probe-<random>` that
is removed afterwards.

## 4.3 Remote change detection

WebDAV has no change feed. Strategy, in order of preference (chosen by probe):

1. **Incremental crawl**: if folder etag/mtime propagates upwards, PROPFIND only folders whose
   etag changed since the last crawl.
2. **Full crawl**: `Depth: 1` breadth-first over all folders, throttled by the request limiter.
   ~900 folders → ~900 requests; at the default 5 req/s that is ~3 minutes, so the full crawl
   is not the "fast path" — it runs on a longer interval (default 15 min) and on demand.
3. Targeted re-list of folders touched by our own operations or by the last local changes.

Remote identity of a file version: `etag` if present, otherwise `(size, mtime)`.

## 4.4 Provider profiles

A profile holds provider-specific defaults (endpoint, auth hint, known limits, probe results).
The engine itself is provider-neutral.

| Profile | Endpoint | Notes |
|---|---|---|
| Mail.ru Cloud (primary) | `https://webdav.cloud.mail.ru/` | App password for WebDAV; content hash = [§3.3](03-legacy-state.md); limits per probe ([§18](18-open-questions.md)). |
| Generic WebDAV | user-defined | Defaults only; everything else from the probe. |
