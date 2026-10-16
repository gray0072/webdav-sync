# 18. Open questions (resolve with `wds probe` / manual checks)

1. Does the provider's WebDAV still require a paid plan, and what are its file-size and rate limits?
2. Does PROPFIND expose any content hash? (If not, remote-side equality relies on size + etag/mtime.)
3. Does folder etag/mtime change on descendant changes (incremental crawl possible)?
4. Can mtime be preserved/set on upload?
5. Does WebDAV `DELETE` go to the cloud trash?
6. Does remote `getlastmodified` match `FILES_RECORDS.time` of the legacy remote tree? (Decides how
   the imported base is matched against the first remote crawl: by size + mtime, or by size only + hash verification.
   Setting `importedBaseMatch`, [§3.5](03-legacy-state.md); default `SizeOnly` until answered.)
7. Meaning of `.cloud_ss`.

`wds probe` answers 2–4 automatically (and whether MOVE keeps etag/mtime); reference answers for
`rclone serve webdav` and Mail.ru are in [§4.2](04-remote-webdav.md).

Mail.ru answers (test account, 2026-10-16):

1. WebDAV works on the test account with an app password (quota 8 GB). File-size and rate limits: not tested.
2. Yes: `getetag` is the content hash of [§3.3](03-legacy-state.md) (`remoteHashProperty = {DAV:}getetag`).
3. No: folders have no etag / `getlastmodified` → full crawl only.
4. Not on `PUT` (`X-OC-Mtime` ignored); `PROPPATCH` of `Win32LastModifiedTime` or `getlastmodified` sets it.
   Not needed: the base stores both mtimes ([§4.6](04-remote-webdav.md)).
5. Yes: deleted items land in the cloud trash; same names get suffixes there (`big.bin`, `big (1).bin`, `big (2).bin`).
6. Moot while the remote hash is known: hash comparison takes precedence over `importedBaseMatch`
   ([§3.5](03-legacy-state.md)); `SizeOnly` stays the default for servers without a hash. Confirmed on the main
   account: `getetag` equals the legacy `hash` for 43 697 files up to 2 GB.
