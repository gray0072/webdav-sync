# 13. Edge cases (must be handled and covered by tests)

Test names start with `EdgeN_` for the case they cover (e.g. `Edge24_Incomplete_remote_listing_…`).
Open items of the prototype: [§19.8](19-prototype-readiness.md).

Names and paths:

1. Case-only renames; two remote names differing only in case (if the remote is case-sensitive) → rename one locally with ` (case conflict)`.
2. Names invalid on Windows: `<>:"/\|?*`, control chars, trailing dot/space, reserved names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`) → not downloaded, reported as warning with an offer to rename remotely.
3. Names invalid on the remote (probe-defined) → not uploaded, warning.
4. Paths longer than 260 chars → always use long-path APIs; remote URL length limit → warning.
5. Unicode normalization: compare keys in NFC; never rename files just because of NFC/NFD.
6. Cyrillic and other non-ASCII names; URL encoding of `%`, `#`, `+`, spaces in WebDAV hrefs.

Content and timing:

7. Zero-byte files.
8. Files larger than the remote's max file size → skipped with an error entry.
9. File changed while being hashed/uploaded/downloaded → abort op, re-queue.
10. File locked by another process (Office, antivirus) → retry later, show "waiting for file" after N tries.
11. Office save pattern (write temp, rename original, rename temp) → appears as modify of the same path after debounce, not delete+create.
12. Rapid repeated saves → coalesced by the quiet period.
13. mtime-only change (touch, copy that preserved content) → hash equal → base update only.
14. Clock skew between machine and server; DST; FAT 2-s mtime precision.
15. Same content in many files → content-reuse and move detection must not mismatch (prefer same name/parent).

Structure:

16. Folder renamed with thousands of files → single remote MOVE.
17. Folder deleted locally while a new file was added into it remotely (and vice versa) → [§6.2](06-sync-algorithm.md) folder rule.
18. File replaced by folder with the same name (and vice versa).
19. Move into an excluded folder / out of the sync root.
20. Empty folders are synced.
21. Hard links: synced as regular files, FileId-based move detection must not treat two links as a move.
22. Very deep trees and very large folders (10k+ entries in one PROPFIND response).

Environment:

23. Network loss / DNS failure / captive portal (HTML instead of XML) → offline state, back off, no deletes inferred from failed listings.
24. **A failed or partial remote listing must never be interpreted as deletions.** Only a complete, successful listing of a folder can produce remote deletes for that folder.
25. Auth failure (`401`, revoked app password) → pause pair, tray error, ask for new password.
26. Quota exceeded (`507`) → stop uploads, keep downloads, error.
27. Server `5xx`, `429`, timeouts → backoff.
28. Sleep/hibernate/resume, network change → rescan + re-crawl.
29. Local disk full → stop downloads, error.
30. Sync root on a removable/unmounted drive → root sanity guard.
31. Two app instances / two pairs on the same folder → refuse.
32. Process crash or power loss mid-op → journal recovery ([§6.7](06-sync-algorithm.md)), temp files cleaned up.
33. `FileSystemWatcher` buffer overflow → full local rescan.
34. Read-only / hidden / system attributes → sync content, preserve read-only locally; never sync attribute-only changes.
35. Remote item changed during download (etag changed) → discard temp, retry.
36. Another client edits the cloud concurrently (phone `Camera Uploads`) → normal remote changes.
37. System time jump → timers use monotonic clock.

Sync state ([§15.2](15-config-storage.md)):

38. User deletes `.webdavsync\` → new pair, union-mode initial sync, warning; never a mass delete.
39. Synced folder copied elsewhere together with `.webdavsync\` and added as a second pair → pair id
    already in use → refuse, offer to reset the copy's state.
40. Synced folder restored from a backup with an older `state.db` → normal 3-way merge
    (base older than reality is safe: it only causes extra comparisons); mass-delete guard still applies.
41. Synced folder moved to another path/volume → confirm, discard FileIds, rescan.
42. `state.db` corrupted or locked by another process (antivirus, backup tool) → stop, offer restore from `backup\`.
43. App folder moved/copied → `data\` moves with it; the password is not in that user's/machine's Credential Manager → ask for password.

Selective sync ([§12.2](12-filters.md)):

44. Folder excluded while transfers in it are running → cancel them, discard partial downloads.
45. Folder excluded with unsynced local changes and "remove local copy" → upload first; on failure keep the files.
46. Folder re-included after its local copy was kept and both sides changed meanwhile → union mode, conflicts, no deletes.
47. Local top-level folder created with the name of an excluded remote folder → ask, never merge silently.
48. Selection changed while the app was not running (settings edited by hand) → detected via the `meta` snapshot, applied as above.
49. Excluded folder renamed remotely → selection follows if detected as a move; otherwise the new name is a new folder per policy.
