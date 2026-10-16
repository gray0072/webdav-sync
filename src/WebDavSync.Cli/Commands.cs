using System.CommandLine;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Legacy;
using WebDavSync.Core.Local;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;
using WebDavSync.Core.State;

namespace WebDavSync.Cli;

/// <summary>CLI commands (§19.2). Exit codes: 0 ok, 1 error, 2 partially done, 3 stopped by a guard or the user.</summary>
internal static class Commands
{
    public static Command Probe(Option<string?> dataDir)
    {
        var url = new Option<string>("--url") { Description = "WebDAV endpoint, e.g. https://webdav.cloud.mail.ru/", Required = true };
        var user = new Option<string>("--user") { Description = "Login (password in WDS_PASSWORD).", Required = true };
        var rootOption = new Option<string>("--root") { Description = "Remote folder for the probe folder.", DefaultValueFactory = _ => "/" };
        var command = new Command("probe", "Detect WebDAV capabilities (§4.2) and store the report in data\\.") { url, user, rootOption };
        command.SetAction((parse, ct) => Run(parse.GetValue(dataDir), "probe", async ctx =>
        {
            using var client = ctx.CreateClient(parse.GetValue(url)!, parse.GetValue(user)!);
            var report = await new WebDavProbe(client, ctx.Logger).RunAsync(parse.GetValue(url)!, parse.GetValue(user)!, parse.GetValue(rootOption)!, ct);
            foreach (var check in report.Checks)
            {
                var mark = check.Ok switch { true => "yes", false => "no ", null => " ? " };
                Console.WriteLine($"  [{mark}] {check.Name,-45} {check.Detail}");
            }

            var file = Path.Combine(ctx.Paths.DataDir, $"probe-{new Uri(parse.GetValue(url)!).Host}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(file, report.ToJson(), ct);
            Console.WriteLine($"Report saved to {file}");
            return report.Checks.Any(c => c.Ok == false && c.Name.StartsWith("Auth", StringComparison.Ordinal)) ? 1 : 0;
        }));
        return command;
    }

    public static Command Pair(Option<string?> dataDir)
    {
        var name = new Option<string>("--name") { Description = "Short pair name.", Required = true };
        var local = new Option<string>("--local") { Description = "Local root folder.", Required = true };
        var url = new Option<string>("--url") { Description = "WebDAV endpoint.", Required = true };
        var remoteRoot = new Option<string>("--remote-root") { Description = "Remote root folder.", DefaultValueFactory = _ => "/" };
        var user = new Option<string>("--user") { Description = "Login.", Required = true };
        var mode = new Option<SyncMode>("--mode") { Description = "Operating mode (§10).", DefaultValueFactory = _ => SyncMode.ApproveAll };
        var importLegacy = new Option<bool>("--import-legacy") { Description = "Import <local>\\.cloud.db as the baseline (§3.4)." };
        var reset = new Option<bool>("--reset-state") { Description = "Take over a folder that belongs to another pair (§13 #39)." };
        var hashProperty = new Option<string>("--remote-hash-property")
        {
            Description = "PROPFIND property with the content hash, e.g. {DAV:}getetag; 'auto' = from the newest probe report for the endpoint, 'none' = unknown.",
            DefaultValueFactory = _ => "auto",
        };
        var add = new Command("add", "Create a sync pair.") { name, local, url, remoteRoot, user, mode, importLegacy, reset, hashProperty };
        add.SetAction((parse, _) => Run(parse.GetValue(dataDir), "pair add", ctx =>
        {
            var pairName = parse.GetValue(name)!;
            if (ctx.Settings.Find(pairName) is not null)
            {
                throw new CliException($"Pair '{pairName}' already exists.");
            }

            var localRoot = Path.GetFullPath(parse.GetValue(local)!);
            if (ctx.Settings.Pairs.Any(p => string.Equals(Path.GetFullPath(p.LocalRoot), localRoot, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CliException($"Another pair already syncs '{localRoot}' (§13 #31).");
            }

            var pair = new PairSettings
            {
                Name = pairName,
                LocalRoot = localRoot,
                RemoteUrl = parse.GetValue(url)!,
                RemoteRoot = parse.GetValue(remoteRoot)!,
                User = parse.GetValue(user)!,
                Mode = parse.GetValue(mode),
                RemoteHashProperty = parse.GetValue(hashProperty) switch
                {
                    "auto" => ctx.FindProbedHashProperty(parse.GetValue(url)!),
                    "none" => null,
                    var explicitName => ParseHashProperty(explicitName!),
                },
            };
            PairSetup.InitializeLocal(pair, parse.GetValue(reset));
            if (parse.GetValue(importLegacy))
            {
                PrintImport(PairSetup.ImportLegacy(pair, force: false));
            }

            ctx.Settings.Pairs.Add(pair);
            ctx.SaveSettings();
            Console.WriteLine($"Pair '{pair.Name}' ({pair.Id}) created: {pair.LocalRoot} <-> {pair.RemoteUrl.TrimEnd('/')}{pair.RemoteRootNormalized}, mode {pair.Mode}.");
            Console.WriteLine($"Remote content hash: {pair.RemoteHashProperty ?? "unknown (same-size files are downloaded to compare)"}");
            Console.WriteLine($"Settings: {ctx.Paths.SettingsFile}");
            return Task.FromResult(0);
        }));

        var list = new Command("list", "List pairs.");
        list.SetAction((parse, _) => Run(parse.GetValue(dataDir), "pair list", ctx =>
        {
            foreach (var p in ctx.Settings.Pairs)
            {
                Console.WriteLine($"{p.Name,-16} {p.Id}  {p.LocalRoot} <-> {p.RemoteUrl.TrimEnd('/')}{p.RemoteRootNormalized}  mode={p.Mode}");
            }

            if (ctx.Settings.Pairs.Count == 0)
            {
                Console.WriteLine("No pairs. Create one with 'wds pair add'.");
            }

            return Task.FromResult(0);
        }));

        return new Command("pair", "Manage sync pairs.") { add, list };
    }

    private static (Option<string[]> KeepLocal, Option<string[]> KeepRemote) KeepOptions() =>
    (
        new Option<string[]>("--keep-local") { Description = "File (relative path) whose local version overwrites the remote one if they differ; repeatable (§6.4)." },
        new Option<string[]>("--keep-remote") { Description = "File (relative path) whose remote version overwrites the local one if they differ; repeatable (§6.4)." }
    );

    private static Dictionary<string, Side> KeepSide(ParseResult parse, Option<string[]> keepLocal, Option<string[]> keepRemote)
    {
        var result = new Dictionary<string, Side>(StringComparer.Ordinal);
        foreach (var (option, side) in new[] { (keepLocal, Side.Local), (keepRemote, Side.Remote) })
        {
            foreach (var path in parse.GetValue(option) ?? [])
            {
                var key = PathKey.FromPath(path.Replace('\\', '/').Trim('/'));
                if (result.TryGetValue(key, out var other) && other != side)
                {
                    throw new CliException($"'{path}' is given with both --keep-local and --keep-remote.");
                }

                result[key] = side;
            }
        }

        return result;
    }

    private static string ParseHashProperty(string name)
    {
        try
        {
            return XName.Get(name).ToString();
        }
        catch (Exception ex) when (ex is ArgumentException or XmlException)
        {
            throw new CliException($"--remote-hash-property: '{name}' is not a property name in Clark notation, e.g. {{DAV:}}getetag.");
        }
    }

    public static Command Import(Option<string?> dataDir)
    {
        var rootArg = new Argument<string>("root") { Description = "Folder that contains .cloud.db." };
        var pairOption = new Option<string?>("--pair") { Description = "Write the imported baseline into this pair's state." };
        var force = new Option<bool>("--force") { Description = "Replace an existing base." };
        var command = new Command("import", "Analyze the legacy client's .cloud.db (read-only, on a copy).") { rootArg, pairOption, force };
        command.SetAction((parse, _) => Run(parse.GetValue(dataDir), "import", ctx =>
        {
            var root = Path.GetFullPath(parse.GetValue(rootArg)!);
            if (parse.GetValue(pairOption) is { } pairName)
            {
                var pair = ctx.RequirePair(pairName);
                if (!string.Equals(Path.GetFullPath(pair.LocalRoot), root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliException($"Pair '{pair.Name}' syncs '{pair.LocalRoot}', not '{root}'.");
                }

                PrintImport(PairSetup.ImportLegacy(pair, parse.GetValue(force)));
                ctx.SaveSettings();
                Console.WriteLine($"Baseline written to pair '{pair.Name}'.");
                return Task.FromResult(0);
            }

            var db = Path.Combine(root, LegacyImporter.DbFileName);
            if (!File.Exists(db))
            {
                throw new CliException($"'{db}' not found.");
            }

            PrintImport(LegacyImporter.Import(db, Path.Combine(Path.GetTempPath(), "wds-import")));
            return Task.FromResult(0);
        }));
        return command;
    }

    public static Command Scan(Option<string?> dataDir)
    {
        var rootArg = new Argument<string>("root") { Description = "Local folder to scan." };
        var hash = new Option<LocalHashMode>("--hash") { Description = "Which files to hash.", DefaultValueFactory = _ => LocalHashMode.All };
        var sample = new Option<int>("--sample") { Description = "With .cloud.db: additionally hash up to N unchanged files and compare with the legacy hash." };
        var command = new Command("scan", "Local snapshot with FileId, size, mtime and content hash; compares with .cloud.db if present.") { rootArg, hash, sample };
        command.SetAction((parse, ct) => Run(parse.GetValue(dataDir), "scan", async ctx =>
        {
            var root = Path.GetFullPath(parse.GetValue(rootArg)!);
            var progress = new ConsoleProgress();
            var snapshot = await new LocalScanner(SyncFilter.Empty, ctx.Logger).ScanAsync(
                root,
                new Dictionary<string, BaseEntry>(),
                parse.GetValue(hash),
                null,
                new Progress<ScanProgress>(p => progress.Report($"{p.Files} files, {p.Folders} folders, hashed {SafetyGuards.Size(p.HashedBytes)}")),
                ct);
            progress.Done();
            var files = snapshot.Entries.Values.Where(e => !e.IsDir).ToList();
            Console.WriteLine($"{files.Count} files ({SafetyGuards.Size(files.Sum(f => f.Size))}), {snapshot.Entries.Count - files.Count} folders, {snapshot.SkippedKeys.Count} skipped, complete={snapshot.IsComplete}");
            Console.WriteLine($"FileId available: {files.Count(f => f.FileId is not null)}/{files.Count}; locked: {files.Count(f => f.Locked)}");
            PrintWarnings("Scan warnings", snapshot.Warnings, 20);

            var db = Path.Combine(root, LegacyImporter.DbFileName);
            if (File.Exists(db))
            {
                var legacy = LegacyImporter.Import(db, Path.Combine(Path.GetTempPath(), "wds-import"));
                int unchanged = 0, hashEqual = 0, hashDiffer = 0, inodeEqual = 0, changed = 0, missing = 0;
                foreach (var item in legacy.LocalTree.Values.Where(i => !i.IsDir))
                {
                    if (!snapshot.Entries.TryGetValue(item.Key, out var e) || e.IsDir)
                    {
                        missing++;
                        continue;
                    }

                    if (e.Size != item.Size || !Mtime.Equal(e.MtimeUtc, item.Mtime))
                    {
                        changed++;
                        continue;
                    }

                    unchanged++;
                    inodeEqual += e.FileId == item.Inode ? 1 : 0;
                    if (e.Hash is null && hashEqual + hashDiffer < parse.GetValue(sample))
                    {
                        try
                        {
                            e = e with { Hash = await ContentHash.ComputeFileAsync(PathKey.ToLocalFull(root, e.Path), ct) };
                        }
                        catch (IOException)
                        {
                        }
                    }

                    if (e.Hash is { } h)
                    {
                        if (h == item.Hash)
                        {
                            hashEqual++;
                        }
                        else
                        {
                            hashDiffer++;
                        }
                    }
                }

                Console.WriteLine($"vs .cloud.db local tree: {unchanged} unchanged (size+mtime), {changed} changed, {missing} missing on disk, {snapshot.Entries.Values.Count(e => !e.IsDir && !legacy.LocalTree.ContainsKey(e.Key))} new on disk");
                Console.WriteLine($"  unchanged files: hash equal {hashEqual}, hash differs {hashDiffer}, FileId == inode {inodeEqual}");
            }

            return 0;
        }));
        return command;
    }

    public static Command Plan(Option<string?> dataDir)
    {
        var pairArg = new Argument<string>("pair") { Description = "Pair name or id." };
        var full = new Option<bool>("--full") { Description = "List every op (default: first 25 per group)." };
        var (keepLocal, keepRemote) = KeepOptions();
        var command = new Command("plan", "Print the three-way plan; nothing is executed.") { pairArg, full, keepLocal, keepRemote };
        command.SetAction((parse, ct) => Run(parse.GetValue(dataDir), "plan", async ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(pairArg)!);
            using var client = ctx.CreateClient(pair);
            using var engine = SyncEngine.Open(pair, client, ctx.Logger);
            var progress = new ConsoleProgress();
            var cycle = await engine.PlanAsync(KeepSide(parse, keepLocal, keepRemote), progress, ct);
            progress.Done();
            PlanPrinter.Print(cycle, parse.GetValue(full));
            return cycle.Guards.HasErrors ? 3 : 0;
        }));
        return command;
    }

    public static Command Sync(Option<string?> dataDir)
    {
        var pairArg = new Argument<string>("pair") { Description = "Pair name or id." };
        var once = new Option<bool>("--once") { Description = "Run one cycle (the only mode of the prototype).", Required = true };
        var dryRun = new Option<bool>("--dry-run") { Description = "Plan only." };
        var maxOps = new Option<int?>("--max-ops") { Description = "Execute at most N ops (MaxOpsPerCycle)." };
        var mode = new Option<SyncMode?>("--mode") { Description = "Override the pair's mode for this run." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Approve the plan without asking (guard trips still need --accept-guards)." };
        var acceptGuards = new Option<bool>("--accept-guards") { Description = "Accept mass-delete / mass-overwrite guard trips without asking." };
        var full = new Option<bool>("--full") { Description = "List every op." };
        var (keepLocal, keepRemote) = KeepOptions();
        var command = new Command("sync", "Plan, approve and execute one cycle.") { pairArg, once, dryRun, maxOps, mode, yes, acceptGuards, full, keepLocal, keepRemote };
        command.SetAction((parse, ct) => Run(parse.GetValue(dataDir), "sync", async ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(pairArg)!);
            var effectiveMode = parse.GetValue(dryRun) ? SyncMode.DryRun : parse.GetValue(mode) ?? pair.Mode;
            using var client = ctx.CreateClient(pair);
            using var engine = SyncEngine.Open(pair, client, ctx.Logger);
            var progress = new ConsoleProgress();
            var cycle = await engine.PlanAsync(KeepSide(parse, keepLocal, keepRemote), progress, ct);
            progress.Done();
            PlanPrinter.Print(cycle, parse.GetValue(full));

            if (cycle.Guards.HasErrors)
            {
                Console.WriteLine("Stopped by safety guard errors.");
                return 3;
            }

            if (effectiveMode == SyncMode.DryRun)
            {
                Console.WriteLine("Dry run: nothing executed.");
                return 0;
            }

            if (!cycle.Plan.Ops.Any() && !cycle.CreateRemoteMarker)
            {
                Console.WriteLine("Up to date.");
                engine.Store.InitialSyncDone |= cycle.Local.IsComplete && cycle.Remote.IsComplete;
                return 0;
            }

            if (cycle.Guards.RequiresApproval && !parse.GetValue(acceptGuards) &&
                !Confirm("Safety guard tripped (see above). Type 'yes' to continue anyway: ", "yes"))
            {
                Console.WriteLine("Stopped by the safety guard.");
                return 3;
            }

            // Union-mode plans are always shown for approval, regardless of the mode (§7.2).
            var needing = cycle.UnionMode && effectiveMode is SyncMode.Guarded or SyncMode.Auto
                ? cycle.Plan.ActionOps.ToList()
                : Approval.NeedingApproval(cycle.Plan, effectiveMode);
            Func<SyncOp, bool> approved = _ => true;
            if (needing.Count > 0 && !parse.GetValue(yes))
            {
                var answer = Ask($"{needing.Count} ops need approval. Execute? [y]es / [s]afe ops only / [N]o: ");
                if (answer is "s" or "safe")
                {
                    var rejected = needing.ToHashSet();
                    approved = op => !rejected.Contains(op) || op.IsSafe;
                }
                else if (answer is not ("y" or "yes"))
                {
                    Console.WriteLine("Rejected: nothing executed.");
                    return 3;
                }
            }

            var selected = Approval.Select(cycle.Plan, approved, parse.GetValue(maxOps) ?? pair.MaxOpsPerCycle);
            Console.WriteLine($"Executing {selected.Count(o => !o.IsBaseOnly)} ops ({selected.Count(o => o.IsBaseOnly)} base updates)...");
            var result = await engine.ExecuteAsync(cycle, selected, new Progress<Core.Execution.OpResult>(r =>
            {
                if (!r.Op.IsBaseOnly || r.Status != JournalStatus.Done)
                {
                    Console.WriteLine($"  {Status(r.Status)} {r.Op.Type,-12} {r.Op.DisplayPath}{(r.Message is null ? string.Empty : "  — " + r.Message)}");
                }
            }), ct);
            Console.WriteLine($"Done {result.Done}, failed {result.Failed}, skipped {result.Skipped}.{(result.AbortReason is null ? string.Empty : " Aborted: " + result.AbortReason)}");
            Console.WriteLine(engine.Store.InitialSyncDone ? "Initial sync: done." : "Initial sync: not complete yet (union mode continues).");
            return result.AllDone && selected.Count == cycle.Plan.Ops.Count ? 0 : result.AbortReason is null ? 2 : 1;
        }));
        return command;
    }

    public static Command Status(Option<string?> dataDir)
    {
        var pairArg = new Argument<string>("pair") { Description = "Pair name or id." };
        var command = new Command("status", "Show the pair state.") { pairArg };
        command.SetAction((parse, _) => Run(parse.GetValue(dataDir), "status", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(pairArg)!);
            var state = new StatePaths(pair.LocalRoot);
            Console.WriteLine($"Pair {pair.Name} ({pair.Id}): {pair.LocalRoot} <-> {pair.RemoteUrl.TrimEnd('/')}{pair.RemoteRootNormalized}, mode {pair.Mode}");
            if (!File.Exists(state.StateDb))
            {
                Console.WriteLine("No state.db.");
                return Task.FromResult(1);
            }

            using var store = StateStore.Open(state.StateDb);
            Console.WriteLine($"Base entries: {store.BaseCount}; initial sync done: {store.InitialSyncDone}; open conflicts: {store.OpenConflictCount}");
            Console.WriteLine($"Last cycle: {store.GetMeta(MetaKeys.LastCycle) ?? "-"}");
            Console.WriteLine($"Legacy import: {store.GetMeta(MetaKeys.LegacyImport) ?? "-"}");
            return Task.FromResult(0);
        }));
        return command;
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<int> Run(string? dataDir, string name, Func<CliContext, Task<int>> body)
    {
        CliContext? ctx = null;
        try
        {
            ctx = new CliContext(dataDir, name);
            return await body(ctx);
        }
        catch (Exception ex) when (ex is CliException or PairStoppedException or RemoteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            ctx?.Logger.LogErrorSafe(ex, name);
            return ex is PairStoppedException ? 3 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    private static void LogErrorSafe(this Microsoft.Extensions.Logging.ILogger logger, Exception ex, string command) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex, "Command {Command} failed", command);

    private static void PrintImport(LegacyImportResult r)
    {
        Console.WriteLine($"Legacy client {r.GeneralInfo.GetValueOrDefault("client_version")}, db_version {r.GeneralInfo.GetValueOrDefault("db_version")}");
        Console.WriteLine($"Local tree:  {r.LocalFiles} files, {r.LocalFolders} folders");
        Console.WriteLine($"Remote tree: {r.RemoteFiles} files, {r.RemoteFolders} folders");
        Console.WriteLine($"Agreement:   {r.BothEqual} identical (base), {r.BothDifferent} differ, {r.LocalOnly} local only, {r.RemoteOnly} remote only");
        Console.WriteLine($"Ignored folders (selective sync): {(r.IgnoredFolders.Count == 0 ? "none" : string.Join(", ", r.IgnoredFolders))}");
        PrintWarnings("Import warnings", r.Warnings, 20);
    }

    internal static void PrintWarnings(string title, IReadOnlyList<string> warnings, int limit)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{title} ({warnings.Count}):");
        foreach (var w in warnings.Take(limit))
        {
            Console.WriteLine("  ! " + w);
        }

        if (warnings.Count > limit)
        {
            Console.WriteLine($"  ... and {warnings.Count - limit} more (see the log)");
        }
    }

    private static string Status(JournalStatus status) => status switch
    {
        JournalStatus.Done => "ok  ",
        JournalStatus.Skipped => "skip",
        _ => "FAIL",
    };

    private static string Ask(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine(prompt + "(no interactive input: treated as No)");
            return "n";
        }

        Console.Write(prompt);
        return (Console.ReadLine() ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
    }

    private static bool Confirm(string prompt, string expected) => Ask(prompt) == expected;
}

/// <summary>Single-line progress on the console.</summary>
internal sealed class ConsoleProgress : IProgress<string>
{
    private int _lastLength;

    public void Report(string value)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        var text = value.Length > 110 ? value[..110] : value;
        Console.Write("\r" + text.PadRight(_lastLength));
        _lastLength = text.Length;
    }

    public void Done()
    {
        if (_lastLength > 0 && !Console.IsOutputRedirected)
        {
            Console.Write("\r" + new string(' ', _lastLength) + "\r");
        }

        _lastLength = 0;
    }
}
