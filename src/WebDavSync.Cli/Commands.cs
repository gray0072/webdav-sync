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

        var showPair = new Argument<string>("pair") { Description = "Pair name or id." };
        var show = new Command("show", "Print all settings of a pair (§15.5).") { showPair };
        show.SetAction((parse, _) => Run(parse.GetValue(dataDir), "pair show", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(showPair)!);
            foreach (var (name, value) in SettingValues(pair))
            {
                Console.WriteLine($"{name,-26} {value}{(PairSettingsEditor.Fixed.Contains(name) ? "  (fixed)" : string.Empty)}");
            }

            return Task.FromResult(0);
        }));

        var setPair = new Argument<string>("pair") { Description = "Pair name or id." };
        var assignments = new Argument<string[]>("settings")
        {
            Description = "One or more name=value, e.g. mode=ApproveAll maxOpsPerCycle=20 (none clears an optional value), minFreeDiskSpace=4GB.",
            Arity = ArgumentArity.OneOrMore,
        };
        var set = new Command("set", "Change settings of a pair; all or nothing.") { setPair, assignments };
        set.SetAction((parse, _) => Run(parse.GetValue(dataDir), "pair set", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(setPair)!);
            var before = SettingValues(pair);
            try
            {
                PairSettingsEditor.Set(pair, parse.GetValue(assignments)!);
            }
            catch (ArgumentException ex)
            {
                throw new CliException(ex.Message);
            }

            if (ctx.Settings.Pairs.Any(p => p != pair && string.Equals(p.Name, pair.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CliException($"Pair '{pair.Name}' already exists.");
            }

            var changes = SettingValues(pair).Where(kv => before[kv.Key] != kv.Value).ToList();
            if (changes.Count == 0)
            {
                Console.WriteLine("No changes.");
                return Task.FromResult(0);
            }

            ctx.SaveSettings();
            foreach (var (name, value) in changes)
            {
                Console.WriteLine($"{name}: {before[name]} -> {value}");
                ctx.Logger.LogInformationSafe("Pair {Pair}: {Setting} changed from {Old} to {New}", pair.Id, name, before[name], value);
            }

            return Task.FromResult(0);
        }));

        return new Command("pair", "Manage sync pairs.") { add, list, show, set };
    }

    /// <summary>Settings of a pair as displayed by <c>pair show</c>, in schema order; unset optional values are <c>none</c>.</summary>
    private static Dictionary<string, string> SettingValues(PairSettings pair)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(pair, AppSettings.JsonOptions);
        foreach (var property in typeof(PairSettings).GetProperties().Where(p => p.CanWrite))
        {
            var name = AppSettings.JsonOptions.PropertyNamingPolicy!.ConvertName(property.Name);
            result[name] = json.TryGetProperty(name, out var value)
                ? value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => value.GetString()!,
                    System.Text.Json.JsonValueKind.Array when property.PropertyType == typeof(List<AutoApproveRule>) =>
                        "[" + string.Join("; ", pair.AutoApprove) + "]",
                    System.Text.Json.JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(v => v.ToString())) + "]",
                    _ => value.GetRawText(),
                }
                : "none";
        }

        return result;
    }

    private static (Option<string[]> KeepLocal, Option<string[]> KeepRemote) KeepOptions() =>
    (
        new Option<string[]>("--keep-local") { Description = "File (relative path) whose local version overwrites the remote one if they differ; repeatable (§6.4)." },
        new Option<string[]>("--keep-remote") { Description = "File (relative path) whose remote version overwrites the local one if they differ; repeatable (§6.4)." }
    );

    private static Option<string[]> HoldOption() =>
        new("--hold") { Description = "Put the ops of this path (whole subtree) into the held list: not proposed again until the item changes; repeatable (§10)." };

    private static List<string> Keys(ParseResult parse, Option<string[]> option) =>
        (parse.GetValue(option) ?? []).Select(p => PathKey.FromPath(p.Replace('\\', '/').Trim('/'))).ToList();

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
        var hold = HoldOption();
        var command = new Command("plan", "Print the three-way plan; nothing is executed (except --hold).") { pairArg, full, keepLocal, keepRemote, hold };
        command.SetAction((parse, ct) => Run(parse.GetValue(dataDir), "plan", async ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(pairArg)!);
            using var client = ctx.CreateClient(pair);
            using var engine = SyncEngine.Open(pair, client, ctx.Logger);
            var progress = new ConsoleProgress();
            var cycle = await engine.PlanAsync(new PlanOptions { KeepSide = KeepSide(parse, keepLocal, keepRemote), Hold = Keys(parse, hold) }, progress, ct);
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
        var hold = HoldOption();
        var command = new Command("sync", "Plan, approve and execute one cycle.") { pairArg, once, dryRun, maxOps, mode, yes, acceptGuards, full, keepLocal, keepRemote, hold };
        command.SetAction((parse, ct) => Run(parse.GetValue(dataDir), "sync", async ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(pairArg)!);
            var effectiveMode = parse.GetValue(dryRun) ? SyncMode.DryRun : parse.GetValue(mode) ?? pair.Mode;
            using var client = ctx.CreateClient(pair);
            using var engine = SyncEngine.Open(pair, client, ctx.Logger);
            var progress = new ConsoleProgress();
            var cycle = await engine.PlanAsync(new PlanOptions { KeepSide = KeepSide(parse, keepLocal, keepRemote), Hold = Keys(parse, hold) }, progress, ct);
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

            // Union-mode plans are always shown for approval, regardless of the mode and rules (§7.2).
            var needing = Approval.NeedingApproval(cycle.Plan, effectiveMode, cycle.UnionMode, pair.AutoApprove);
            Func<SyncOp, bool> approved = _ => true;
            if (needing.Count > 0 && !parse.GetValue(yes))
            {
                var answer = Ask($"{needing.Count} ops need approval. Execute? [y]es / [s]afe ops only / [h]old them, run the rest / [N]o: ");
                if (answer is "s" or "safe")
                {
                    var rejected = needing.ToHashSet();
                    approved = op => !rejected.Contains(op) || op.IsSafe;
                }
                else if (answer is "h" or "hold")
                {
                    var rejected = needing.ToHashSet();
                    approved = op => !rejected.Contains(op);
                    var held = engine.Hold(cycle, needing);
                    Console.WriteLine($"Held {held.Count} ops: not proposed again until the item changes ('wds held {pair.Name}' lists them).");
                    if (held.Count < needing.Count)
                    {
                        Console.WriteLine($"{needing.Count - held.Count} moves/renames cannot be held: nothing after them runs this cycle.");
                    }
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

    public static Command Held(Option<string?> dataDir)
    {
        var pairArg = new Argument<string>("pair") { Description = "Pair name or id." };
        var release = new Option<string[]>("--release") { Description = "Release the held ops of this path (whole subtree); repeatable." };
        var all = new Option<bool>("--all") { Description = "Release all held ops." };
        var command = new Command("held", "List or release held ops (rejected earlier, §10).") { pairArg, release, all };
        command.SetAction((parse, _) => Run(parse.GetValue(dataDir), "held", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(pairArg)!);
            var state = new StatePaths(pair.LocalRoot);
            if (!File.Exists(state.StateDb))
            {
                throw new CliException($"No state.db for pair '{pair.Name}'.");
            }

            using var store = StateStore.Open(state.StateDb);
            var held = store.LoadHeld();
            var keys = Keys(parse, release);
            if (parse.GetValue(all) || keys.Count > 0)
            {
                var released = held.Where(h => parse.GetValue(all) || keys.Any(k => PathKey.IsSelfOrUnder(h.Key, k))).ToList();
                store.Release(released);
                foreach (var h in released)
                {
                    Console.WriteLine($"released  {h.Type,-12} {h.DisplayPath}");
                    ctx.Logger.LogInformationSafe("Released held {Type} {Path}", h.Type, h.DisplayPath);
                }

                Console.WriteLine($"{released.Count} released; the next plan proposes them again.");
                return Task.FromResult(0);
            }

            foreach (var h in held)
            {
                Console.WriteLine($"{h.HeldAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {h.Type,-12} {h.DisplayPath}  [{h.Reason}]");
            }

            Console.WriteLine(held.Count == 0 ? "No held ops." : $"{held.Count} held ops. Release with --release <path> or --all.");
            return Task.FromResult(0);
        }));
        return command;
    }

    public static Command Rule(Option<string?> dataDir)
    {
        var listPair = new Argument<string>("pair") { Description = "Pair name or id." };
        var list = new Command("list", "List the auto-approve rules of a pair.") { listPair };
        list.SetAction((parse, _) => Run(parse.GetValue(dataDir), "rule list", ctx =>
        {
            PrintRules(ctx.RequirePair(parse.GetValue(listPair)!));
            return Task.FromResult(0);
        }));

        var addPair = new Argument<string>("pair") { Description = "Pair name or id." };
        var type = new Option<OpType>("--type") { Description = "Op type, e.g. Download, Upload, MkdirLocal, DeleteRemote.", Required = true };
        var folder = new Option<string>("--folder") { Description = "Relative folder (whole subtree); omit for the whole pair.", DefaultValueFactory = _ => string.Empty };
        var overwrite = new Option<bool>("--overwrite") { Description = "Also approve uploads/downloads that replace an existing item." };
        var add = new Command("add", "Always approve ops of this type in this folder (§10); guards and the initial sync still ask.") { addPair, type, folder, overwrite };
        add.SetAction((parse, _) => Run(parse.GetValue(dataDir), "rule add", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(addPair)!);
            var opType = parse.GetValue(type);
            if (opType is OpType.UpdateBase or OpType.ForgetBase)
            {
                throw new CliException($"{opType} ops never need approval.");
            }

            var rule = new AutoApproveRule
            {
                Type = opType,
                Folder = parse.GetValue(folder)!.Replace('\\', '/').Trim('/'),
                Overwrite = parse.GetValue(overwrite),
            };
            pair.AutoApprove.Add(rule);
            ctx.SaveSettings();
            ctx.Logger.LogInformationSafe("Pair {Pair}: auto-approve rule added: {Rule}", pair.Id, rule.ToString());
            PrintRules(pair);
            return Task.FromResult(0);
        }));

        var removePair = new Argument<string>("pair") { Description = "Pair name or id." };
        var number = new Argument<int>("number") { Description = "Rule number from 'wds rule list'." };
        var remove = new Command("remove", "Remove an auto-approve rule.") { removePair, number };
        remove.SetAction((parse, _) => Run(parse.GetValue(dataDir), "rule remove", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(removePair)!);
            var n = parse.GetValue(number);
            if (n < 1 || n > pair.AutoApprove.Count)
            {
                throw new CliException($"No rule #{n}; the pair has {pair.AutoApprove.Count}.");
            }

            var rule = pair.AutoApprove[n - 1];
            pair.AutoApprove.RemoveAt(n - 1);
            ctx.SaveSettings();
            ctx.Logger.LogInformationSafe("Pair {Pair}: auto-approve rule removed: {Rule}", pair.Id, rule.ToString());
            PrintRules(pair);
            return Task.FromResult(0);
        }));

        return new Command("rule", "Auto-approve (\"approve and remember\") rules (§10).") { list, add, remove };
    }

    private static void PrintRules(PairSettings pair)
    {
        for (var i = 0; i < pair.AutoApprove.Count; i++)
        {
            Console.WriteLine($"{i + 1,3}. {pair.AutoApprove[i]}");
        }

        if (pair.AutoApprove.Count == 0)
        {
            Console.WriteLine("No auto-approve rules.");
        }
    }

    public static Command Secret(Option<string?> dataDir)
    {
        var setPair = new Argument<string>("pair") { Description = "Pair name or id." };
        var fromEnv = new Option<bool>("--from-env") { Description = $"Take the password from {CliContext.PasswordVariable} instead of asking." };
        var set = new Command("set", "Store the pair's app password in data\\secrets.dat, encrypted for this Windows user (DPAPI, §15.1).") { setPair, fromEnv };
        set.SetAction((parse, _) => Run(parse.GetValue(dataDir), "secret set", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(setPair)!);
            var password = parse.GetValue(fromEnv)
                ? Environment.GetEnvironmentVariable(CliContext.PasswordVariable) ?? string.Empty
                : ReadHidden($"App password for {pair.User} at {pair.RemoteUrl}: ");
            if (password.Length == 0)
            {
                throw new CliException("Empty password: nothing stored.");
            }

            ctx.Secrets.Set(pair.Id, password);
            ctx.Logger.LogInformationSafe("Pair {Pair}: password stored in secrets.dat", pair.Id);
            Console.WriteLine($"Password of pair '{pair.Name}' stored in {ctx.Paths.SecretsFile} (DPAPI, this Windows user only).");
            return Task.FromResult(0);
        }));

        var removePair = new Argument<string>("pair") { Description = "Pair name or id." };
        var remove = new Command("remove", "Delete the pair's stored password.") { removePair };
        remove.SetAction((parse, _) => Run(parse.GetValue(dataDir), "secret remove", ctx =>
        {
            var pair = ctx.RequirePair(parse.GetValue(removePair)!);
            Console.WriteLine(ctx.Secrets.Remove(pair.Id) ? $"Password of pair '{pair.Name}' removed." : $"No stored password for pair '{pair.Name}'.");
            return Task.FromResult(0);
        }));

        return new Command("secret", "Stored app passwords (secrets.dat).") { set, remove };
    }

    private static string ReadHidden(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return (Console.ReadLine() ?? string.Empty).Trim();
        }

        Console.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                text.Length = Math.Max(0, text.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
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
            Console.WriteLine($"Password: {(ctx.Secrets.Get(pair.Id) is not null ? "stored in secrets.dat" : Environment.GetEnvironmentVariable(CliContext.PasswordVariable) is { Length: > 0 } ? CliContext.PasswordVariable : "missing")}");
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

    private static void LogInformationSafe(this Microsoft.Extensions.Logging.ILogger logger, string message, params object?[] args) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, message, args);

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

        // Discard type-ahead (e.g. a stray Enter from pasting the command during a long crawl): only an answer typed
        // after the prompt counts.
        while (Console.KeyAvailable)
        {
            Console.ReadKey(intercept: true);
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
