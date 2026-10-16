# 19. Prototype readiness

Status as of 2026-10-16: **CLI prototype implemented** (milestones 1–3 minimal) and **verified on the real
Mail.ru cloud** with a disposable test pair (runbook §19.9 steps 1–5 done) and a dry run of `D:\Cloud` against
the main account (step 6 done). Next step: the first real cycles on `D:\Cloud` after the user's decisions (§19.9).

## 19.1 Environment (checked)

- .NET SDK `10.0.401` installed. Docker installed (for the WebDAV test server).
- Sync folder `D:\Cloud` exists with legacy `.cloud.db` / `.cloud_ss`.
- Legacy DB facts confirmed: content hash algorithm ([§3.3](03-legacy-state.md)) matches real files;
  `key == lower(name)` for every row (roots have empty key); 3 names are not in NFC → the
  normalization edge case ([§13](13-edge-cases.md) #5) is real.

## 19.2 Prototype scope and status

CLI only (`src/WebDavSync.Cli`, executable `wds`) on top of `src/WebDavSync.Core`. No tray app,
no watcher, no scheduler.

| Command | Done when | Status |
|---|---|---|
| `wds probe --url --user [--root]` | Prints a capability report ([§4.2](04-remote-webdav.md)) and stores it; answers [§18](18-open-questions.md) items 2–6 for Mail.ru. | Done; verified against rclone and Mail.ru. |
| `wds pair add` / `pair list` | Creates a pair: `settings.json` entry, local `pair.json`, `state.db`; optional `--import-legacy`. | Done. |
| `wds import <root> [--pair]` | Reads `.cloud.db` from a copy, prints tree stats, both-trees agreement, ignored folders; with `--pair` writes the baseline. | Done; real `D:\Cloud`: 43 885 / 43 883 files, 893 folders, 44 776 base entries, 2 local-only. |
| `wds scan <root> [--hash] [--sample N]` | Local snapshot with FileId/size/mtime and content hash; hashes match `.cloud.db` for unchanged files. | Done; real `D:\Cloud`: 43 698 unchanged, FileId == `inode` for all, 300/300 sampled hashes equal. |
| `wds plan <pair>` | Three-way plan printed grouped by op type with reasons; nothing executed. | Done. |
| `wds sync <pair> --once [--dry-run] [--max-ops N]` | Executes the plan with console approval, journal, base commit, safety guards. | Done. |
| `wds status <pair>` | Base size, initial sync state, conflicts, last cycle. | Done. |

Acceptance:

- Scenarios A, B, C ([§7](07-scenarios.md)) pass against the in-memory remote (unit tests) and against
  `rclone serve webdav` in Docker (integration tests); the CLI flow probe → pair add → plan → sync was run
  manually against rclone. The test pair `D:\CloudTest` ⇄ `/webdavsync-test` on the real Mail.ru cloud (second,
  disposable account): `scripts/smoke-real.ps1` — probe, C+E, A, B all PASS, with and without the remote hash
  (runs in `D:\CloudTest\run2`, `run3`).
- `plan` for the real `D:\Cloud` with the imported baseline: checked **offline** (legacy remote tree as a
  stand-in for the remote, [§3.5](03-legacy-state.md)): 232 items to delete remotely (184 files, 48 folders)
  → mass-delete guard trips. Re-check with the real crawl.
- Tests: 146 unit + 6 integration, all green (`dotnet test`).

## 19.3 Needs the user (before touching the real cloud)

Status: #1, #2, #4 done (test account `/webdavsync-test`, 2026-10-16); #3 pending; `importedBaseMatch` is moot on
Mail.ru because the remote hash is known ([§3.5](03-legacy-state.md)).

1. Create a WebDAV app password in the provider account; confirm WebDAV works on the current plan
   (e.g. one `PROPFIND` with curl). Without it only the local/Docker part can be built.
2. Create the test remote folder `/webdavsync-test` and local `D:\CloudTest` (small, disposable data).
3. Decide on [§18](18-open-questions.md) #5 manually: after a test `DELETE` via WebDAV, check the cloud trash in the web UI.
4. Run `wds probe` against the real endpoint and record the results in [§4.4](04-remote-webdav.md) / [§18](18-open-questions.md);
   choose `importedBaseMatch` ([§3.5](03-legacy-state.md)) from the answer to §18 #6.

## 19.4 Decisions taken as defaults (change here if needed)

| Topic | Default |
|---|---|
| Solution | `WebDavSync.slnx`, `global.json` (SDK 10.0.401, `rollForward: latestFeature`, test runner Microsoft.Testing.Platform), `Directory.Build.props` (nullable, warnings as errors, latest C#), `Directory.Packages.props` (central package versions), `.editorconfig`, `.gitignore` (incl. `bin/`, `obj/`, `.vs/`, `.kilo/`). |
| Tests | xUnit v3 (4.x, on Microsoft.Testing.Platform: `dotnet test`); unit tests use an in-memory `IRemoteStore` fake; integration tests use `rclone serve webdav` in Docker (skipped when Docker is not running). |
| CLI parsing | `System.CommandLine` 2.x. |
| Logging | `Microsoft.Extensions.Logging` + Serilog file sink into `data\logs\wds-<date>.log` (30 days). |
| SQLite | `Microsoft.Data.Sqlite`, WAL, raw SQL (no ORM). |
| Key normalization | `Key = NFC(path).ToLowerInvariant()`, `/` separator, no leading slash. Matches legacy `key`. |
| Hash | Streaming content hash ([§3.3](03-legacy-state.md)) as `ContentHash` value type (40 uppercase hex). |
| Execution | Sequential (one op at a time), global request rate limit; no parallel transfers yet. |
| Tray (later) | WinForms `NotifyIcon`. `src/WebDavSync.App` is not created yet. |

## 19.5 Spec gaps (closed)

1. Domain model and planner contract → [§6.9](06-sync-algorithm.md).
2. Folder rules as a table (D1–D9) → [§6.2](06-sync-algorithm.md).
3. Move detection rules (M1–M10, confirmation of FileId matches, tiers, ties) → [§6.3](06-sync-algorithm.md).
4. "Complete listing" and "known absent" → [§6.8](06-sync-algorithm.md).
5. Imported base vs first remote crawl → [§3.5](03-legacy-state.md) (default `SizeOnly`; revisit after the probe).
6. `settings.json` schema → [§15.5](15-config-storage.md).
7. HTTP error taxonomy and retry policy → [§4.5](04-remote-webdav.md).
8. Legacy test fixture → `tests/WebDavSync.Core.Tests/Support/LegacyFixture.cs` (same schema as [§3.1](03-legacy-state.md), synthetic data).
9. Remote mtime handling → [§4.6](04-remote-webdav.md).

## 19.6 Not needed for the prototype

Tray UI ([§14](14-tray-ui.md)), watcher and timing ([§8](08-timing.md)), bandwidth limits and pause
([§11](11-bandwidth-pause.md)), selective sync on the fly ([§12.2](12-filters.md)), toasts, schedule,
diagnostics export, `secrets.dat` (prototype reads the password from the environment variable
`WDS_PASSWORD`; never from command-line args or files in the repo).

## 19.7 Findings during the prototype

- The FileId must be read with the native 52-byte `BY_HANDLE_FILE_INFORMATION` layout (`FILETIME` is 4-byte
  aligned); with that, it equals the legacy `inode` for every unchanged file.
- A FileId match alone is not enough evidence for a move ([§6.3](06-sync-algorithm.md)).
- Servers may change etags on MOVE; moved subtrees are compared by size + mtime ([§4.3](04-remote-webdav.md)).
- After a legacy import the remote marker does not exist yet although the initial sync counts as done; marker
  creation is tracked separately (`remote_marker_created`, [§9](09-safety-guards.md) #1).
- The mass-delete percentage trips on small pairs ([§9](09-safety-guards.md) #2).
- Mail.ru `getetag` equals the legacy content hash ([§3.3](03-legacy-state.md)): remote equality, imported base
  matching and upload/download verification are exact; `Compare` downloads are not needed there
  (`remoteHashProperty`, [§15.5](15-config-storage.md)). Other quirks: [§4.4](04-remote-webdav.md).

## 19.8 Known gaps in the prototype (next milestones)

- Held list for rejected ops and "approve and remember" rules ([§10](10-modes-approval.md)).
- Content reuse instead of download ([§6.3](06-sync-algorithm.md)); `Range` resume of downloads ([§6.6](06-sync-algorithm.md)).
- Journal re-validation of interrupted ops ([§6.7](06-sync-algorithm.md)) — currently re-planned from scratch.
- Parallel transfers, bandwidth limits, adaptive backoff on 429/503 ([§11](11-bandwidth-pause.md)).
- Remote names invalid on the remote side (probe-defined, [§13](13-edge-cases.md) #3), max file size (#8), remote URL length (#4).
- Two remote names differing only in case are skipped with a warning, not renamed locally ([§13](13-edge-cases.md) #1).
- `*.tmp` files being written and the stability check before upload ([§5](05-local.md), [§12.1](12-filters.md)).
- Recycle Bin deletion (`SHFileOperation`) is not covered by automated tests (tests use `StateTrash`); it is
  exercised by `scripts/smoke-real.ps1` (verified against rclone: the file landed in the Recycle Bin).
- Incremental crawl ([§4.3](04-remote-webdav.md)); the full crawl is used every cycle.

## 19.9 Real cloud runbook

Progress (2026-10-16, test account): steps 1–5 done — password as a user env var, probe recorded
([§4.2](04-remote-webdav.md), [§18](18-open-questions.md)), smoke test PASS, `remoteHashProperty` wired, Mail.ru
quirks covered by `WebDavClientTests`. §18 #5: deletes go to the cloud trash. Step 6 done on the main account
(login in the user env var `WDS_LOGIN`, pair `cloud`, remote root `/` — the 8 top-level folders match exactly,
data dir `%LOCALAPPDATA%\wds-cloud`); result in [§3.5](03-legacy-state.md). Next: the user decides on the 227 remote
deletes, the 2 conflicts and the download, then `ApproveAll` with `maxOpsPerCycle = 20`.

The user provides the WebDAV app password and creates two **empty** folders: local `D:\CloudTest` and remote
`/webdavsync-test`. Commands below assume the repo root and `wds` = `dotnet run --project src/WebDavSync.Cli --`
(or the Release build `src\WebDavSync.Cli\bin\Release\net10.0\wds.dll`).

1. **Password.** Best: the user sets it once as a user environment variable, so it never appears in the chat:
   `[Environment]::SetEnvironmentVariable('WDS_PASSWORD', '<app password>', 'User')`, then restarts VS Code.
   If it is given in the chat: use it only as `$env:WDS_PASSWORD = '...'` inside the command; never write it to
   files, settings, logs, commits, or `wds` arguments. Check: `if ($env:WDS_PASSWORD) { 'set' }`.
2. **Access + probe.** `wds probe --url https://webdav.cloud.mail.ru/ --user <login> --root /webdavsync-test`.
   401 → wrong password or WebDAV not available on the plan ([§18](18-open-questions.md) #1).
3. **Record the probe** (report in `data\probe-*.json`): Mail.ru reference result in [§4.2](04-remote-webdav.md),
   profile notes in [§4.4](04-remote-webdav.md), answers to [§18](18-open-questions.md) #2–4. If a hash property
   is found, add the `remoteHashProperty` setting (§19.8).
4. **Smoke test.** `.\scripts\smoke-real.ps1 -Url https://webdav.cloud.mail.ru/ -User <login>` — probe, then
   scenarios C+E, A, B in subfolders `C`, `A`, `B` of both test folders; expect all PASS. It refuses non-empty
   folders and anything that looks like `D:\Cloud`. Then the user checks the cloud trash in the web UI for
   `C/big.bin` (deleted on the server by the sync's `DeleteRemote`) → answer [§18](18-open-questions.md) #5.
   Re-run: add `-Cleanup` (removes `A`, `B`, `C` on both sides) or clean by hand first.
5. **Fix Mail.ru quirks** (href format, trailing slashes, 409/405 on MKCOL, missing etags, date formats, rate
   limits): reproduce each server response in a unit test with the fake `HttpMessageHandler`
   (`WebDavClientTests`) and update [§4](04-remote-webdav.md).
6. **Real `D:\Cloud`, dry run only — ask the user first** (creates `D:\Cloud\.webdavsync\`, nothing else changes):
   - Agree on the remote root of `D:\Cloud` (the legacy client probably synced the whole account, `/`): compare
     the top-level names of `wds import D:\Cloud` with a listing of the candidate root.
   - `wds pair add --name cloud --local D:\Cloud --url https://webdav.cloud.mail.ru/ --remote-root <root> --user <login> --mode DryRun --import-legacy`
   - `wds plan cloud --full` (full crawl, ~900 folders ≈ 3 min at 5 req/s). Expected, from the offline check
     ([§3.5](03-legacy-state.md)): ~44.5k `UpdateBase`, 3 `Upload`, ~232 items in `DeleteRemote`, mass-delete
     guard trips. Many `Download`s would mean the remote changed since the legacy client stopped; with
     `importedBaseMatch = SizeAndMtime` they would mean `getlastmodified` ≠ legacy `time` ([§18](18-open-questions.md) #6).
   - No `sync` on `D:\Cloud` without an explicit go-ahead; the next rollout step is `ApproveAll` with
     `maxOpsPerCycle = 20` ([§16](16-testing-rollout.md) #4).
