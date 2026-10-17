using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Execution;
using WebDavSync.Core.Local;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;
using WebDavSync.Core.State;

namespace WebDavSync.Core.Engine;

/// <summary>Root sanity / setup errors that stop the pair (§9 #1).</summary>
public sealed class PairStoppedException(string message) : Exception(message);

public sealed record CyclePlan(
    string CycleId,
    Plan Plan,
    GuardReport Guards,
    LocalSnapshot Local,
    RemoteSnapshot Remote,
    int TrackedItems,
    bool UnionMode,
    bool CreateRemoteRoot,
    bool CreateRemoteMarker,
    IReadOnlyList<string> Notes)
{
    /// <summary>Ops left out of <see cref="Plan"/> by the held list (§10).</summary>
    public IReadOnlyList<SyncOp> Held { get; init; } = [];

    /// <summary>The remote snapshot is a refreshed cached one, not a full crawl (§4.3 #3).</summary>
    public bool RemoteFromCache { get; init; }
}

/// <summary>Per-cycle user input for planning.</summary>
public sealed record PlanOptions
{
    /// <summary>One-off "keep local / keep remote" choices by key (§6.4), see <see cref="PlannerSettings.KeepSide"/>.</summary>
    public IReadOnlyDictionary<string, Side> KeepSide { get; init; } = new Dictionary<string, Side>();

    /// <summary>Keys whose ops (whole subtree) are put into the held list before guards and approval (§10).</summary>
    public IReadOnlyCollection<string> Hold { get; init; } = [];

    /// <summary>
    /// Remote snapshot of an earlier cycle: only <see cref="RefreshFolders"/> are re-listed instead of a full crawl
    /// (§4.3 #3). Falls back to a full crawl when a re-listed folder is gone.
    /// </summary>
    public RemoteSnapshot? CachedRemote { get; init; }

    /// <summary>Folder keys to re-list with <see cref="CachedRemote"/>.</summary>
    public IReadOnlyCollection<string> RefreshFolders { get; init; } = [];
}

/// <summary>
/// One pair, one cycle at a time (§6.1): lock → root sanity → crawl + scan → plan → guards; the caller approves,
/// then <see cref="ExecuteAsync"/> runs the selected ops.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly FileStream _lock;
    private readonly ILogger _logger;

    private SyncEngine(PairSettings pair, StatePaths state, StateStore store, IRemoteStore remote, FileStream lockFile, ILogger logger)
    {
        Pair = pair;
        State = state;
        Store = store;
        Remote = remote;
        _lock = lockFile;
        _logger = logger;
    }

    public PairSettings Pair { get; }

    public StatePaths State { get; }

    public StateStore Store { get; }

    public IRemoteStore Remote { get; }

    public SyncFilter Filter => SyncFilter.For(Pair);

    /// <summary>Opens the pair: local root sanity, engine lock, state DB, journal recovery, daily backup.</summary>
    public static SyncEngine Open(PairSettings pair, IRemoteStore remote, ILogger logger)
    {
        var state = new StatePaths(pair.LocalRoot);
        if (!Directory.Exists(state.LocalRoot))
        {
            throw new PairStoppedException($"Local root '{state.LocalRoot}' does not exist (unplugged drive?). Nothing is synced.");
        }

        var marker = state.ReadMarker() ??
                     throw new PairStoppedException($"'{state.PairFile}' is missing: the folder is not set up for this pair (or the state was deleted). Run 'wds pair add' again.");
        if (!string.Equals(marker.PairId, pair.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PairStoppedException($"'{state.PairFile}' belongs to pair {marker.PairId}, settings expect {pair.Id} (copied folder?).");
        }

        FileStream lockFile;
        try
        {
            lockFile = new FileStream(state.EngineLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new PairStoppedException($"Another engine is running this pair ('{state.EngineLock}' is locked).");
        }

        StateStore? store = null;
        try
        {
            store = StateStore.Open(state.StateDb);
            var storedId = store.GetMeta(MetaKeys.PairId);
            if (storedId is null)
            {
                store.SetMeta(MetaKeys.PairId, pair.Id);
            }
            else if (!string.Equals(storedId, pair.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new PairStoppedException($"state.db belongs to pair {storedId}, settings expect {pair.Id}.");
            }

            var interrupted = store.RecoverJournal();
            if (interrupted > 0)
            {
                logger.LogWarning("{Count} ops were interrupted by a crash; they are re-planned from the current state", interrupted);
            }

            LocalFs.CleanTemp(state);
            store.BackupDaily(state.BackupDir);
            return new SyncEngine(pair, state, store, remote, lockFile, logger);
        }
        catch
        {
            store?.Dispose();
            lockFile.Dispose();
            throw;
        }
    }

    public Task<CyclePlan> PlanAsync(IProgress<string>? status = null, CancellationToken ct = default) =>
        PlanAsync(new Dictionary<string, Side>(), status, ct);

    /// <param name="keepSide">One-off "keep local / keep remote" choices by key (§6.4), see <see cref="PlannerSettings.KeepSide"/>.</param>
    public Task<CyclePlan> PlanAsync(IReadOnlyDictionary<string, Side> keepSide, IProgress<string>? status = null, CancellationToken ct = default) =>
        PlanAsync(new PlanOptions { KeepSide = keepSide }, status, ct);

    public async Task<CyclePlan> PlanAsync(PlanOptions options, IProgress<string>? status = null, CancellationToken ct = default)
    {
        var cycleId = Guid.NewGuid().ToString("N")[..12];
        var notes = new List<string>();
        var remoteRoot = Pair.RemoteRootNormalized;
        var initialDone = Store.InitialSyncDone;
        var markerCreated = Store.GetMeta(MetaKeys.RemoteMarkerCreated) == "1";

        // Remote root sanity (§9 #1): once the marker was created, root and marker must exist. A pair with a
        // baseline (initial sync done or legacy import) never creates a missing root.
        status?.Report("Checking remote root...");
        var rootItem = await Remote.StatAsync(remoteRoot, ct).ConfigureAwait(false);
        var createRoot = false;
        var createMarker = false;
        if (rootItem is null)
        {
            if (initialDone || markerCreated)
            {
                throw new PairStoppedException($"Remote root '{remoteRoot}' is missing. Nothing is synced (never 'sync the emptiness').");
            }

            createRoot = createMarker = true;
            notes.Add($"Remote root '{remoteRoot}' does not exist; it will be created.");
        }
        else
        {
            var marker = await ReadRemoteMarkerAsync(ct).ConfigureAwait(false);
            if (marker is null)
            {
                if (markerCreated)
                {
                    throw new PairStoppedException($"Remote marker '{StatePaths.RemoteMarkerFile(remoteRoot)}' is missing. Wrong remote folder?");
                }

                createMarker = true;
                notes.Add("Remote marker .webdavsync/pair.json will be created.");
            }
            else if (!string.Equals(marker.PairId, Pair.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new PairStoppedException($"Remote folder '{remoteRoot}' belongs to pair {marker.PairId}, expected {Pair.Id}.");
            }
        }

        var baseEntries = Store.LoadBase();
        var filter = Filter;

        RemoteSnapshot remote;
        var remoteFromCache = false;
        if (rootItem is null)
        {
            remote = new RemoteSnapshot(new Dictionary<string, RemoteEntry>(), new HashSet<string> { string.Empty });
        }
        else
        {
            var scanner = new RemoteScanner(Remote, filter, _logger, Pair.MaxParallelRequests);
            RemoteSnapshot? refreshed = null;
            if (options.CachedRemote is { } cached)
            {
                status?.Report($"Checking {options.RefreshFolders.Count} remote folders...");
                refreshed = await scanner.RefreshAsync(cached, remoteRoot, options.RefreshFolders, ct).ConfigureAwait(false);
            }

            if (refreshed is not null)
            {
                remote = refreshed;
                remoteFromCache = true;
                notes.Add($"Remote: cached snapshot, {options.RefreshFolders.Count} folders re-listed.");
            }
            else
            {
                status?.Report("Crawling remote...");
                remote = await scanner
                    .CrawlAsync(remoteRoot, string.Empty, new InlineProgress<int>(n => status?.Report($"Crawling remote: {n} folders")), ct)
                    .ConfigureAwait(false);
            }
        }

        status?.Report("Scanning local...");
        var local = await new LocalScanner(filter, _logger).ScanAsync(
            State.LocalRoot,
            baseEntries,
            LocalHashMode.Needed,
            key => remote.Entries.TryGetValue(key, out var r) && !r.IsDir,
            new InlineProgress<ScanProgress>(p => status?.Report($"Scanning local: {p.Files} files, {p.Folders} folders, hashed {SafetyGuards.Size(p.HashedBytes)}")),
            ct).ConfigureAwait(false);

        var trustFileIds = true;
        var markerSerial = State.ReadMarker()?.VolumeSerial;
        var currentSerial = LocalFs.GetVolumeSerial(State.LocalRoot);
        if (markerSerial is not null && currentSerial is not null && markerSerial != currentSerial)
        {
            trustFileIds = false;
            notes.Add("The folder is on another volume than at setup: FileIds are ignored (no local move detection this cycle).");
        }

        var unionMode = !initialDone;
        if (unionMode)
        {
            notes.Add("Initial sync (union mode): nothing is deleted on either side.");
        }

        var plan = Planner.Create(new PlannerInput(local, remote, baseEntries, new PlannerSettings
        {
            UnionMode = unionMode,
            ConflictWinner = Pair.ConflictWinner,
            Now = DateTime.Now,
            MachineName = Environment.MachineName,
            TrustLocalFileIds = trustFileIds,
            ImportedBaseMatch = Pair.ImportedBaseMatch,
            MaxCompareDownloadBytes = Pair.MaxCompareDownloadBytes,
            Filter = filter,
            KeepSide = options.KeepSide,
        }));
        (plan, var held) = ApplyHeldList(plan, options.Hold, local, remote, notes);

        RemoteQuota? quota = null;
        try
        {
            quota = await Remote.GetQuotaAsync(ct).ConfigureAwait(false);
        }
        catch (RemoteException ex) when (!ex.StopsCycle)
        {
            _logger.LogInformation("Quota not available: {Message}", ex.Message);
        }

        long? free = null;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(State.LocalRoot)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogInformation("Free disk space not available: {Message}", ex.Message);
        }

        var guards = SafetyGuards.Evaluate(plan, baseEntries.Count, Pair, quota, free);
        _logger.LogInformation(
            "Cycle {Cycle}: plan {Ops} ops ({Actions} actions), {Warnings} warnings, guards: {Trips} trips, {Errors} errors",
            cycleId, plan.Ops.Count, plan.ActionOps.Count(), plan.Warnings.Count, guards.Trips.Count, guards.Errors.Count);
        return new CyclePlan(cycleId, plan, guards, local, remote, baseEntries.Count, unionMode, createRoot, createMarker, notes) { Held = held, RemoteFromCache = remoteFromCache };
    }

    /// <summary>Puts ops into the held list (§10): they are not proposed again until their item changes on either side.</summary>
    public IReadOnlyList<HeldItem> Hold(CyclePlan cycle, IEnumerable<SyncOp> ops)
    {
        var items = ops.Where(HeldList.CanHold).Select(o => HeldList.Hold(o, cycle.Local, cycle.Remote, DateTime.UtcNow)).ToList();
        Store.Hold(items);
        foreach (var item in items)
        {
            _logger.LogInformation("Held {Type} {Path} ({Reason})", item.Type, item.DisplayPath, item.Reason);
        }

        return items;
    }

    private (Plan Plan, IReadOnlyList<SyncOp> Held) ApplyHeldList(Plan plan, IReadOnlyCollection<string> hold, LocalSnapshot local, RemoteSnapshot remote, List<string> notes)
    {
        if (hold.Count > 0)
        {
            var now = DateTime.UtcNow;
            var items = new List<HeldItem>();
            foreach (var key in hold)
            {
                var ops = plan.ActionOps.Where(o => PathKey.IsSelfOrUnder(o.Key, key)).ToList();
                items.AddRange(ops.Where(HeldList.CanHold).Select(o => HeldList.Hold(o, local, remote, now)));
                if (ops.Any(o => !HeldList.CanHold(o)))
                {
                    notes.Add($"Hold '{key}': moves and renames cannot be held (the rest of the plan depends on them).");
                }
                else if (ops.Count == 0)
                {
                    notes.Add($"Hold '{key}': no op to hold.");
                }
            }

            Store.Hold(items);
        }

        var result = HeldList.Apply(plan, Store.LoadHeld(), local, remote);

        // An op can be missing because a folder was not listed: release only on complete snapshots.
        if (result.Released.Count > 0 && local.IsComplete && remote.IsComplete)
        {
            Store.Release(result.Released);
            notes.Add($"{result.Released.Count} held items changed or no longer need an op; they are released.");
            _logger.LogInformation("Released {Count} held items: {Keys}", result.Released.Count, string.Join(", ", result.Released.Select(h => h.Key)));
        }

        if (result.Held.Count > 0)
        {
            notes.Add($"{result.Held.Count} ops are held (rejected earlier, item unchanged): 'wds held' lists them.");
        }

        return (result.Plan, result.Held);
    }

    public async Task<ExecutionResult> ExecuteAsync(CyclePlan cycle, IReadOnlyList<SyncOp> ops, IProgress<OpResult>? progress = null, CancellationToken ct = default)
    {
        if (cycle.Guards.HasErrors)
        {
            throw new PairStoppedException("Safety guard errors: " + string.Join(" ", cycle.Guards.Errors));
        }

        if (cycle.CreateRemoteRoot || cycle.CreateRemoteMarker)
        {
            await CreateRemoteMarkerAsync(cycle.CreateRemoteRoot, ct).ConfigureAwait(false);
        }

        var executor = new Executor(new ExecutorContext
        {
            State = State,
            RemoteRoot = Pair.RemoteRootNormalized,
            Remote = Remote,
            Store = Store,
            Settings = Pair,
            Filter = Filter,
            Logger = _logger,
        });
        var result = await executor.ExecuteAsync(ops, cycle.CycleId, progress, ct).ConfigureAwait(false);

        // "First full successful cycle" (§7.6): everything planned ran, on complete snapshots of both sides.
        var full = result.AllDone && ops.Count == cycle.Plan.Ops.Count && cycle.Local.IsComplete && cycle.Remote.IsComplete;
        if (full && !Store.InitialSyncDone)
        {
            Store.InitialSyncDone = true;
            _logger.LogInformation("Initial sync completed; deletions are enabled from the next cycle");
        }

        Store.SetMeta(MetaKeys.LastCycle, JsonSerializer.Serialize(new
        {
            cycle.CycleId,
            At = DateTime.UtcNow,
            Planned = cycle.Plan.Ops.Count,
            Selected = ops.Count,
            result.Done,
            result.Failed,
            result.Skipped,
            result.AbortReason,
            Full = full,
        }));
        return result;
    }

    public async Task<PairMarker?> ReadRemoteMarkerAsync(CancellationToken ct)
    {
        try
        {
            await using var download = await Remote.GetAsync(StatePaths.RemoteMarkerFile(Pair.RemoteRootNormalized), 0, ct).ConfigureAwait(false);
            using var reader = new StreamReader(download.Content, Encoding.UTF8);
            return PairMarker.FromJson(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
        }
        catch (RemoteException ex) when (ex.Kind == RemoteErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task CreateRemoteMarkerAsync(bool createRoot, CancellationToken ct)
    {
        var root = Pair.RemoteRootNormalized;
        if (createRoot)
        {
            var current = string.Empty;
            foreach (var segment in root.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + segment;
                if (await Remote.StatAsync(current, ct).ConfigureAwait(false) is null)
                {
                    await Remote.MkdirAsync(current, ct).ConfigureAwait(false);
                }
            }
        }

        var markerDir = StatePaths.RemoteMarkerDir(root);
        if (await Remote.StatAsync(markerDir, ct).ConfigureAwait(false) is null)
        {
            await Remote.MkdirAsync(markerDir, ct).ConfigureAwait(false);
        }

        var marker = State.ReadMarker() ?? throw new PairStoppedException("Local marker disappeared.");
        var bytes = Encoding.UTF8.GetBytes(marker.ToJson());
        await using var stream = new MemoryStream(bytes);
        await Remote.PutAsync(StatePaths.RemoteMarkerFile(root), stream, bytes.Length, null, ct).ConfigureAwait(false);
        Store.SetMeta(MetaKeys.RemoteMarkerCreated, "1");
        _logger.LogInformation("Created remote marker {Marker}", StatePaths.RemoteMarkerFile(root));
    }

    public void Dispose()
    {
        Store.Dispose();
        _lock.Dispose();
    }
}
