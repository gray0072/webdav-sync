# 4. Remote (WebDAV) model

## 4.1 `IRemoteStore`

```
ListAsync(path)                   -> self + direct children (name, isDir, size, mtime, etag?, hash?, contentType?), complete?
StatAsync(path)                   -> item or null (Depth: 0)
GetAsync(path, offset)            -> stream (+ etag, length)
PutAsync(path, stream, length, mtime?)
MkdirAsync(path)
MoveAsync(from, to, overwrite=false)
DeleteAsync(path)
GetQuotaAsync()                   -> used/available (RFC 4331) if supported
```

The probe (§4.2) is a separate component on top of the raw client (it needs PROPPATCH, `allprop`, `Depth: infinity`).
Paths are absolute remote paths; each segment is percent-encoded (`%`, `#`, `+`, spaces, non-ASCII, §13 #6) and
hrefs are decoded relative to the endpoint path. Basic auth is sent preemptively. Metadata requests have a
100 s timeout; all requests go through the `MaxRequestsPerSecond` limiter (monotonic clock).

## 4.2 Capability probe (run on pair setup and from CLI `probe`)

Detect and persist (`wds probe` saves `data\probe-<host>-<time>.json`; machine-readable keys in brackets):

- Auth OK (Basic auth with an **app password** issued by the provider for WebDAV) [`AuthOk`].
- `PROPFIND Depth: 1` works [`Depth1`]; whether `Depth: infinity` is allowed [`DepthInfinity`].
- Which properties are returned [`RootProperties`, `FileProperties`]: `getetag` [`FileETag`], `getlastmodified`,
  `getcontentlength`, quota props [`QuotaUsedBytes`, `QuotaAvailableBytes`], a property whose value equals our
  content hash of the probe file [`HashProperty`].
- Whether a folder's `getetag`/`getlastmodified` changes when a child / a grandchild changes
  [`FolderChangesOnChild`, `FolderChangesOnDescendant`] (enables incremental remote crawl).
- Whether mtime is taken from `PUT` with `X-OC-Mtime` [`MtimeViaOcHeader`] or can be set with `PROPPATCH`
  `Win32LastModifiedTime` [`MtimeViaProppatchWin32`] / `getlastmodified` [`MtimeViaProppatchDav`].
- `MOVE` of a file keeps etag / mtime [`MoveKeepsETag`, `MoveKeepsMtime`]; `MOVE` of a folder is server-side
  with its content [`MoveFolder`]; `DELETE` works [`Delete`].
- `DELETE` goes to the cloud trash (verify via web UI manually; record as a setting).
- Case sensitivity: create `Probe.txt`, `PROPFIND probe.txt` [`CaseInsensitive`].
- Maximum upload size, behavior on quota exceeded (expect `507`), rate limits (`429`, `503`, `Retry-After`):
  not probed automatically (reported as "not tested").

All probe actions happen inside a dedicated remote folder `<root>/.webdavsync-probe-<random>` that
is removed afterwards.

Reference result, `rclone serve webdav` (local backend, used by the integration tests): no quota, no hash
property, `X-OC-Mtime` honored, PROPPATCH mtime ignored, case-sensitive, `Depth: infinity` allowed, folder mtime
changes on a child change but not on a grandchild change, MOVE keeps etag and mtime.

Reference result, Mail.ru (`https://webdav.cloud.mail.ru/`, test account, 2026-10-16): Basic auth with an app
password works; quota props present; file props `displayname`, `getcontentlength`, `getcontenttype`, `getetag`,
`getlastmodified`, `resourcetype`; **`getetag` is the content hash of [§3.3](03-legacy-state.md)** (40 uppercase hex,
unquoted) → `HashProperty = {DAV:}getetag`; `X-OC-Mtime` ignored (upload time is kept), PROPPATCH
`Win32LastModifiedTime` and `getlastmodified` both set the mtime; case-insensitive; `Depth: infinity` → `400`;
folders have neither etag nor `getlastmodified`, so no incremental crawl; MOVE keeps etag and mtime; MOVE of a
folder and DELETE work.

## 4.3 Remote change detection

WebDAV has no change feed. Strategy, in order of preference (chosen by probe):

1. **Incremental crawl**: if folder etag/mtime propagates upwards, PROPFIND only folders whose
   etag changed since the last crawl.
2. **Full crawl**: `Depth: 1` breadth-first over all folders, throttled by the request limiter.
   ~900 folders → ~900 requests; at the default 5 req/s that is ~3 minutes, so the full crawl
   is not the "fast path" — it runs on a longer interval (default 15 min) and on demand.
   (Prototype: always a full crawl.)
3. Targeted re-list of folders touched by our own operations or by the last local changes.

Remote identity of a file version: `etag` if present on both the item and the base, otherwise `(size, mtime)`.
Exceptions: items inside a subtree moved on the server count as unchanged when size and mtime match (servers may
assign new etags on MOVE, §6.3); after our own folder `MOVE` the executor re-crawls the moved folder and adopts
new etags of files whose size and mtime are unchanged.

Remote names invalid on Windows (§13 #2) and names that differ from another item only in case (on a
case-sensitive server, §13 #1) are not synced and reported; they pin their parent folder (§6.2).

## 4.4 Provider profiles

A profile holds provider-specific defaults (endpoint, auth hint, known limits, probe results).
The engine itself is provider-neutral.

Mail.ru quirks seen so far (all handled):

- `Content-Type: application/xml; charset="utf-8"` — the charset is quoted; .NET Framework (Windows PowerShell 5.1)
  refuses to read such a body as a string, the client reads streams and is not affected.
- Hrefs are absolute paths without a trailing slash for folders; folders carry only `displayname` and `resourcetype`.
- Occasional `504 Gateway Timeout` on a small `PUT`; a retry succeeds (Transient, §4.5).
- A 643 MB `PUT` (main account) failed after ~176 s with "Error while copying content to a stream" (connection
  dropped mid-body); it used to be Offline and stopped the cycle, now it is Transient and the op is retried. Root
  cause (server limit vs network) not known yet; error messages now include the innermost cause and the bytes sent.

| Profile | Endpoint | Notes |
|---|---|---|
| Mail.ru Cloud (primary) | `https://webdav.cloud.mail.ru/` | App password for WebDAV; `getetag` = content hash ([§3.3](03-legacy-state.md)), `remoteHashProperty = {DAV:}getetag`; full crawl only; limits per probe ([§18](18-open-questions.md)). |
| Generic WebDAV | user-defined | Defaults only; everything else from the probe. |

## 4.5 HTTP error taxonomy and retry policy

| Class | Statuses / conditions | Handling |
|---|---|---|
| Transient | 408, 423, 429, 5xx except 507, request timeout, malformed XML, connection lost after part of a `PUT` body was sent, connection lost or stalled (no data for 2 min) while reading a `GET` body (the download resumes, §6.6) | Metadata requests (PROPFIND, GET, MKCOL): up to 4 attempts in the client. Ops: up to 3 attempts in the executor. Backoff `4^(n-1)` s with ±20 % jitter, capped at 5 min; `Retry-After` wins. Then the op fails for this cycle. |
| Permanent | other 4xx (incl. 403) | No retry; op fails, goes to the error list. |
| Auth | 401 | No retry; the cycle stops; the pair needs a new password (§13 #25). |
| Quota | 507 | No retry; uploads stop for the rest of the cycle, downloads continue (§13 #26). |
| Offline | no HTTP response (DNS, refused, reset before any body byte was sent), or a non-XML answer to PROPFIND (captive portal) | The cycle stops; a crawl never turns it into deletions (§13 #23). |
| NotFound | 404, 410 | `Stat` → "absent"; listing of a subfolder → its parent's listing becomes incomplete (§6.8). |
| Conflict | 405 (MKCOL on an existing item), 409 (missing parent), 412 (MOVE target exists) | Caller re-checks the actual state (e.g. MKCOL 405 is fine if a folder exists). |

`PUT` and `MOVE` are not retried by the client (a stream cannot be replayed; a MOVE may have succeeded);
the executor retries the whole op after re-checking its preconditions. Adaptive rate halving on 429/503 (§11)
is not in the prototype.

## 4.6 Remote mtime handling

- `PUT` sends `X-OC-Mtime: <local mtime, Unix seconds>`. Servers that honor it (ownCloud/Nextcloud, rclone)
  keep the local mtime; others (Mail.ru) set the upload time. Either is fine: the base stores the local and the remote
  mtime separately, so change detection never compares mtimes across sides (§9 #8).
- Remote identity stored in base: `etag`, `size`, `mtime` as returned by PROPFIND after the operation.
- Downloaded files get the remote `getlastmodified` as their mtime (setting `SetLocalMtimeFromRemote`, default on);
  the base stores the actual local mtime after the move into place.
