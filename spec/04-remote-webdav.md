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

Plain WebDAV has no change feed. Strategy, in order of preference:

0. **Tree listing** (a store with `IRemoteTreeLister`: the Mail.ru API, [§4.7](#47-mailru-api-tree-listing-and-change-token)).
   A crawl takes every folder's listing from one tree request and lists over WebDAV only the folders missing from it
   (§4.7 says which); when the tree is not available the crawl is the normal full crawl (#2). Between polls the
   remote root's change token is checked every `remoteCheckInterval` (default 1 min, §8): a change starts a cycle
   with a full crawl. The token comes with the tree of the whole pair; it is recorded with every complete full crawl
   (`meta.remote_change_token`) and cleared after an incomplete one.
1. **Incremental crawl**: if folder etag/mtime propagates upwards, PROPFIND only folders whose
   etag changed since the last crawl.
2. **Full crawl**: `Depth: 1` breadth-first over all folders, throttled by the request limiter.
   ~900 folders → ~900 requests; at the default 5 req/s that is ~3 minutes, so the full crawl
   is not the "fast path" — it runs on a longer interval (default 15 min) and on demand.
   (Prototype: always a full crawl.) Up to `maxParallelRequests` listings (default 4) are in flight at once, still
   within `maxRequestsPerSecond`; sequential listings were bound by latency (Mail.ru: ~0.5 s each, ~7 min for 843
   folders). Results are processed one at a time, so the snapshot does not depend on arrival order; a listing error
   that stops the cycle cancels the outstanding ones.
3. Targeted re-list of folders touched by our own operations or by the last local changes. Implemented
   (`RemoteScanner.RefreshAsync`, used by the tray service, §8): the cached snapshot of the last cycle is kept;
   each re-listed folder's direct children are replaced, new subfolders are crawled, cached subfolders that vanished
   or became files are dropped with their subtrees, everything else stays as cached. A key that is not a cached
   folder is replaced by its nearest cached ancestor. If a re-listed folder is gone, the cycle falls back to a full
   crawl. A stale cached part is safe: every op re-checks the remote state before acting and a recursive delete
   re-crawls its subtree (§6.6); remote changes elsewhere are picked up by the next full crawl.

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
| Mail.ru Cloud (primary) | `https://webdav.cloud.mail.ru/` | App password; `getetag` = content hash ([§3.3](03-legacy-state.md)), `remoteHashProperty = {DAV:}getetag`; tree listing and change token over the Mail.ru API when the app password has full access (§4.7), otherwise full crawl only; limits per probe ([§18](18-open-questions.md)). |
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

## 4.7 Mail.ru API (tree listing and change token)

On the Mail.ru endpoint (setting `useMailRuApi`, default on) a pair's remote client is `MailRuStore`
(`RemoteClientFactory`): **every operation still goes through WebDAV**; the Mail.ru Cloud API (the official desktop
client's, undocumented; implemented by rclone `backend/mailru` and yar229/WebDavMailRuCloud) adds two reads:

- **Tree listing** (`IRemoteTreeLister.ListTreeAsync`): one request returns a whole subtree with size, mtime and
  content hash of every file — the same fields as the WebDAV listing on Mail.ru (`getetag` = hash; folders without
  etag and mtime), so snapshots, the base and the planner do not see the difference.
- **Change token** (`GetChangeTokenAsync`): the folder's `grev`, which rises on the folder and every ancestor for any
  change below it (add, overwrite, rename, move — on both ends, delete, mtime change), also for changes made over
  WebDAV, the web UI or other clients. A folder outside the changed path keeps its `grev`.

**Login.** OAuth password grant `POST https://o2.mail.ru/token` (form `client_id=cloud-win`, `grant_type=password`,
`username`, `password`). It needs an **app password with full access ("Mail, Cloud and Calendar")**; a WebDAV-only
app password is refused (`error_code 3`). The full-access password works for WebDAV too, so one password serves the
pair. The access token lives 1 h and is renewed with `grant_type=refresh_token`. Tokens travel in query strings
(`access_token=`, `token=`), so request URLs are never logged.

**Requests.** Change token: `GET https://cloud.mail.ru/api/m1/folder?home=<path>&offset=0&limit=1&access_token=…` →
`body.grev`. Tree: `GET https://dispatcher.cloud.mail.ru/m?token=…` → metaserver URL (first word; cached 20 min), then
`POST <metaserver>?client_id=cloud-win&token=…` with the binary body `uvar 117, string path, uvar depth (100 000),
uvar options (0x1E3), uvar 0`. Numbers are unsigned LEB128; request strings are `uvar length+1, UTF-8, 0`.

**Answer.** Status byte (`0` ok, `1` not found), revision header (version byte + tree id and counters), total and
used space (options 1, 64), fingerprint (`uvar` length + bytes), then records: `1` item, `2` descend into the last
folder item, `3` ascend, `15` `uvar n` + n pairs of `uvar` (skipped), `0` end. An item: `u16` LE head (type =
head & 3: `0` mount, `1` file, `2` folder, `3` shared; head & `0x1000` → 16-byte node id), **one byte** name length
and the UTF-8 name, `uvar` revision (folders: `grev`); a file: `uvar` mtime (Unix s), `uvar` size, 20-byte hash; a
folder: `uvar rev`; a mount: tree id[12], 2 × `uvar`; shared: `uvar`, tree id[12]; non-files then `uvar` direct child
folders and files (option 2) and `uvar` subtree size (option 32). The cloud rejects names over 255 UTF-8 bytes (also
over WebDAV); rclone reads the name length as a varint, which breaks names of 128 bytes and more.

**Safety (invariant #1).** The parser reads exactly: a short answer, trailing bytes, an unknown record or a top item
that is not the requested folder reject the whole answer. A folder's listing is used only when its listed children
match the child counts the server reports for it; otherwise (depth cut, mounted or shared folders, anything
unexpected) that folder is listed over WebDAV. Any API failure (login, HTTP, format, the login challenge
`error_code 13` reported for long rclone sessions) makes the tree "not available": the crawl goes over WebDAV and the
API is tried again after 10 min; a refused login disables it until the pair restarts with a new password (repeated
refused logins could lock the account). Name rules, filters and case duplicates are applied by the scanner exactly
as for WebDAV listings.

**Checked** (test account, 2026-10-18): `wds crawl <pair> --compare` (read-only; crawls both ways and compares the
snapshots) — identical on a tree with Cyrillic names of 254 bytes, `#`, `%`, `+`, spaces and 40 levels; tree
listing of 11.4 k items in one request in 0.6 s (~470 KB); `smoke-real.ps1` all PASS with every crawl from the tree
(0 WebDAV listings, including the subtree re-crawl after a folder move).

**Seen but not used** (candidates for later, all live-tested):

- Commit of uploaded content (`PUT` to the `/u` shard, then binary opcode 103) sets the mtime; re-committing the same
  hash changes the mtime without an upload; content the cloud already has (any account) or of ≤ 20 bytes (the hash is
  the content) is committed by hash without an upload. Mode bits: 1 = create if missing, 2 = fail if it exists
  (mode 3 = create only), 0 = replace only; there is no "replace only if the current hash is X".
- Binary move (opcode 105) **silently overwrites** an existing target, and the overwritten file does not go to the
  trash; JSON `file/move` / `file/rename` with `conflict=strict` refuse (`400 exists`). `file/remove` → trash.
- JSON `api/m1/folder` silently returns at most 8 000 children (page with `offset`, check `count`).
- Sending the last fingerprint with a tree request returns status 12 "not modified" (48 bytes), but the fingerprint
  is account-wide.
- Uploads are one `PUT` without resume, as over WebDAV; a 700 MB upload was dropped near its end there too.
