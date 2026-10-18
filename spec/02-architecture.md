# 2. Architecture

```
┌──────────────────────────────── WebDavSync.App (WinForms, tray) ───────────────────────────┐
│ NotifyIcon · Activity window · Approvals · Conflicts · Settings · Toasts                    │
└───────────────▲────────────────────────────────────────────────────────────────────────────┘
                │ events / commands (in-process)
┌───────────────┴──────────────────────── WebDavSync.Core ───────────────────────────────────┐
│ SyncPairHost ── Scheduler (debounce, poll timers, pause, schedule windows)                   │
│   ├─ LocalWatcher (FileSystemWatcher + periodic scan)   ├─ RemoteScanner (PROPFIND crawl)    │
│   ├─ LocalScanner (FileId, size, mtime, attrs, hash)    ├─ IRemoteStore (WebDAV impl)        │
│   ├─ Planner (3-way diff, move detection, conflicts)    ├─ Throttle (bandwidth, req/s, conc.)│
│   ├─ Approval gate (DryRun / Approve / Guarded / Auto)  ├─ Executor (op queue, retries)      │
│   ├─ Safety guards (mass delete, root check, quota)     └─ Journal (op log, crash recovery)  │
│   └─ StateStore (SQLite: base tree, journal, held items, settings snapshot)                  │
│   └─ LegacyImport (read-only importer of .cloud.db)                                          │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
WebDavSync.Cli — headless: probe, import, scan, plan (dry-run), sync once, dump state.
```

## 2.1 Solution layout

```
src/WebDavSync.Core/        engine, no UI, no WinForms references
src/WebDavSync.App/         tray app (WinForms, net10.0-windows)
src/WebDavSync.Cli/         console tool for debugging and scripting
tests/WebDavSync.Core.Tests/        unit tests (planner decision table, hash, name rules)
tests/WebDavSync.IntegrationTests/  against a local WebDAV test server + temp folders
spec/                       this specification
```

## 2.2 Technology

- .NET 10, C# latest, nullable enabled, `TreatWarningsAsErrors`.
- `Microsoft.Data.Sqlite` for state.
- WebDAV: own thin client over `HttpClient` (PROPFIND/GET/PUT/MKCOL/MOVE/DELETE/PROPPATCH),
  streaming bodies, so throttling, cancellation and retries are fully under our control.
- `Microsoft.Extensions.Hosting` / DI / Logging; rolling file log (Serilog or equivalent).
- Credentials in the Windows Credential Manager (per user, outside the portable app folder), never in plain config ([§15](15-config-storage.md)).
- Recycle Bin via `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/DeleteDirectory(..., RecycleOption.SendToRecycleBin)`
  or `IFileOperation`.
- Single instance per user (named mutex); single engine per pair (`<local root>\.webdavsync\engine.lock`).

## 2.3 Extensibility

- `IRemoteStore` is the only remote abstraction; a future native-API store (provider API with
  server hashes, tree revisions — the old client used `TREE_REV_INFO` revisions) can replace WebDAV
  without planner changes. First step done: `MailRuStore` keeps every operation on WebDAV and adds the tree listing
  and change token of the Mail.ru API through the optional `IRemoteTreeLister` ([§4.7](04-remote-webdav.md)).
- Planner is a pure function `(L, R, B, settings) → Plan` — deterministic and unit-testable.
