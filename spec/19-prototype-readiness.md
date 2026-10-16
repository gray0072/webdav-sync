# 19. Prototype readiness

Status as of 2026-10-16. Start the prototype from this file; update it as items get resolved.

## 19.1 Environment (checked)

- .NET SDK `10.0.401` installed. Docker installed (for the WebDAV test server).
- Sync folder `D:\Cloud` exists with legacy `.cloud.db` / `.cloud_ss`.
- Legacy DB facts confirmed: content hash algorithm ([§3.3](03-legacy-state.md)) matches real files;
  `key == lower(name)` for every row (roots have empty key); 3 names are not in NFC → the
  normalization edge case ([§13](13-edge-cases.md) #5) is real.

## 19.2 Prototype scope

CLI only (`src/WebDavSync.Cli`, executable `wds`) on top of `src/WebDavSync.Core`. No tray app,
no watcher, no scheduler. Covers milestones 1–3 ([§17](17-milestones.md)) minimally:

| Command | Done when |
|---|---|
| `wds probe --url --user` | Prints a capability report ([§4.2](04-remote-webdav.md)) and stores it; answers [§18](18-open-questions.md) items 2–6 for Mail.ru. |
| `wds import <root>` | Reads `.cloud.db` from a copy, prints tree stats, both-trees agreement, ignored folders. |
| `wds scan <root>` | Local snapshot with FileId/size/mtime and content hash; hashes match `.cloud.db` for unchanged files. |
| `wds plan <pair>` | Three-way plan printed grouped by op type with reasons; nothing executed. |
| `wds sync --once <pair> [--dry-run] [--max-ops N]` | Executes the plan with console approval (`ApproveAll`), journal, base commit, safety guards. |

Acceptance: scenarios A, B, C ([§7](07-scenarios.md)) pass on a test pair (`D:\CloudTest` ⇄ remote
`/webdavsync-test`) and against the Docker WebDAV server; `plan` for the real `D:\Cloud` with the
imported baseline produces a plausible plan (expected: ~180 remote deletes caught by the mass-delete guard).

## 19.3 Needs the user (before touching the real cloud)

1. Create a WebDAV app password in the provider account; confirm WebDAV works on the current plan
   (e.g. one `PROPFIND` with curl). Without it only the local/Docker part can be built.
2. Create the test remote folder `/webdavsync-test` and local `D:\CloudTest` (small, disposable data).
3. Decide on [§18](18-open-questions.md) #5 manually: after a test `DELETE` via WebDAV, check the cloud trash in the web UI.

## 19.4 Decisions taken as defaults (change here if needed)

| Topic | Default |
|---|---|
| Solution | `WebDavSync.slnx`, `global.json` (SDK 10.0.401, `rollForward: latestFeature`), `Directory.Build.props` (nullable, warnings as errors, latest C#), `Directory.Packages.props` (central package versions), `.editorconfig`, `.gitignore` (incl. `bin/`, `obj/`, `.vs/`, `.kilo/`). |
| Tests | xUnit v3; unit tests use an in-memory `IRemoteStore` fake; integration tests use `rclone serve webdav` in Docker (skipped when Docker is not running). |
| CLI parsing | `System.CommandLine` 2.x. |
| Logging | `Microsoft.Extensions.Logging` + Serilog file sink into `data\logs\`. |
| SQLite | `Microsoft.Data.Sqlite`, WAL, raw SQL (no ORM). |
| Key normalization | `Key = NFC(path).ToLowerInvariant()`, `/` separator, no leading slash. Matches legacy `key`. |
| Hash | Streaming content hash ([§3.3](03-legacy-state.md)) as `ContentHash` value type (40 uppercase hex). |
| Tray (later) | WinForms `NotifyIcon`. |

## 19.5 Spec gaps to close during the prototype

Not blocking the start, but each must be written into the spec when implemented:

1. **Domain model and planner contract**: types for snapshot entries (local/remote/base), `Op`,
   `Plan`, op reasons; exact planner input/output. Only the idea is in [§6](06-sync-algorithm.md).
2. **Folder rules as a table**: folder create/delete/rename and file↔folder type changes are prose
   in [§6.2](06-sync-algorithm.md); turn into a decision table with test ids.
3. **Move detection rules**: exact matching criteria and tie-breaking when several candidates
   have the same size/hash ([§6.3](06-sync-algorithm.md), [§13](13-edge-cases.md) #15).
4. **"Complete listing" definition**: what makes a remote folder listing trustworthy enough to infer
   deletions (all pages, 207 with all `propstat` 200, no mid-crawl 404 of a parent) — invariant #1 depends on it.
5. **Imported base vs first remote crawl** ([§18](18-open-questions.md) #6): how imported remote entries
   are matched to PROPFIND results (size + mtime, or size + verification). Depends on probe results.
6. **settings.json schema**: consolidate all settings and defaults from [§8](08-timing.md),
   [§9](09-safety-guards.md), [§10](10-modes-approval.md), [§11](11-bandwidth-pause.md),
   [§12](12-filters.md) into one table with names and types.
7. **HTTP error taxonomy**: status → transient / permanent / auth / quota / offline, and the retry policy per class.
8. **Legacy test fixture**: the real `.cloud.db` contains personal data (account email, file names) and
   must not be committed. Write a fixture builder that creates a small DB with the same schema
   ([§3.1](03-legacy-state.md)) for importer tests.
9. **Remote mtime handling**: if the server cannot preserve mtime, what we store as remote identity
   and what mtime downloaded files get (remote time or keep local).

## 19.6 Not needed for the prototype

Tray UI ([§14](14-tray-ui.md)), watcher and timing ([§8](08-timing.md)), bandwidth limits and pause
([§11](11-bandwidth-pause.md)), selective sync on the fly ([§12.2](12-filters.md)), toasts, schedule,
diagnostics export, `secrets.dat` (prototype may read the password from an environment variable
`WDS_PASSWORD`; never from command-line args or files in the repo).
