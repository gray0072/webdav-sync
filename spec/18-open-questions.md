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
`rclone serve webdav` are in [§4.2](04-remote-webdav.md). Mail.ru answers are pending ([§19.3](19-prototype-readiness.md)).
