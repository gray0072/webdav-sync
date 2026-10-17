using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Execution;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;

namespace WebDavSync.Core.Engine;

/// <summary>States of a pair as shown by the tray icon (§14).</summary>
public enum PairState
{
    Idle,
    Waiting,

    /// <summary>Scanning and crawling to plan a cycle: nothing is transferred yet (a quiet icon, §14).</summary>
    Checking,

    /// <summary>Executing ops (the animated icon).</summary>
    Syncing,
    Paused,
    NeedsAttention,
    Error,
    Offline,
}

public sealed record PairStatus(
    PairState State,
    string Text,
    DateTime? LastSyncUtc,
    DateTime? NextRunUtc,
    DateTime? PausedUntilUtc,
    int PendingApprovals,
    int FailedOps,
    string? Error);

/// <summary>Ops of the last cycle that wait for the user (§10), with the plan they came from.</summary>
public sealed record PendingApproval(CyclePlan Cycle, IReadOnlyList<SyncOp> Ops, bool GuardTripped, DateTime CreatedUtc);

public sealed record PairServiceOptions
{
    /// <summary>Watch the local root for changes (off in tests that call <see cref="PairService.NotifyLocalChange"/>).</summary>
    public bool Watch { get; init; } = true;

    /// <summary>Pending approvals older than this are re-planned instead of executed.</summary>
    public TimeSpan MaxPendingAge { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Watcher events for paths our own ops touched are ignored this long after the cycle (§8 echoes).</summary>
    public TimeSpan EchoWindow { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan OfflineRetryMin { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan OfflineRetryMax { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Runs one pair in the background (§8, §14): local changes (quiet period / max batch delay), remote polling, sync now
/// and pause trigger cycles; ops that need approval (§10) wait in <see cref="Pending"/> until the UI approves or rejects them.
/// All engine work happens on one loop, so cycles, approvals and rejections never overlap.
/// </summary>
public sealed class PairService : IAsyncDisposable
{
    private readonly IRemoteStore _remote;
    private readonly ILogger _logger;
    private readonly PairServiceOptions _options;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<Func<CancellationToken, Task>> _requests = new();
    private readonly Dictionary<string, DateTime> _echo = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Task? _loop;

    private bool _syncNow;
    private bool _startup;
    private bool _continue;
    private RemoteSnapshot? _cache;
    private DateTime? _lastFullCrawl;
    private readonly HashSet<string> _refresh = new(StringComparer.Ordinal);
    private DateTime? _firstChange;
    private DateTime? _lastChange;
    private DateTime? _lastCycleEnd;
    private DateTime? _retryAt;
    private bool _retryOnlyManually;
    private TimeSpan _offlineDelay;
    private bool _paused;
    private DateTime? _pausedUntil;
    private PendingApproval? _pending;
    private PairStatus _status;

    public PairService(PairSettings pair, IRemoteStore remote, ILogger logger, PairServiceOptions? options = null)
    {
        Pair = pair;
        _remote = remote;
        _logger = logger;
        _options = options ?? new PairServiceOptions();
        _offlineDelay = _options.OfflineRetryMin;
        _status = new PairStatus(PairState.Idle, "Starting", null, null, null, 0, 0, null);
    }

    public PairSettings Pair { get; }

    public PairStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public PendingApproval? Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    /// <summary>Raised on the service's loop thread; UI code must marshal.</summary>
    public event EventHandler<PairStatus>? StatusChanged;

    /// <summary>Every executed op (Activity, §14). Raised on the loop thread.</summary>
    public event EventHandler<OpResult>? OpCompleted;

    /// <summary>Starts the watcher and the loop; the first cycle runs right away.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        if (_options.Watch)
        {
            StartWatcher();
        }

        lock (_lock)
        {
            _syncNow = true;
            _startup = true;
        }

        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>Re-evaluates the schedule after a settings change (e.g. the poll interval).</summary>
    public void Reschedule() => Wake();

    public void SyncNow()
    {
        lock (_lock)
        {
            _syncNow = true;
            _retryOnlyManually = false;
            _retryAt = null;
        }

        Wake();
    }

    /// <summary>Pauses cycles; <paramref name="duration"/> null = until <see cref="Resume"/>. A running cycle finishes.</summary>
    public void Pause(TimeSpan? duration)
    {
        lock (_lock)
        {
            _paused = true;
            _pausedUntil = duration is { } d ? DateTime.UtcNow + d : null;
        }

        SetStatus(PairState.Paused, PausedText());
        Wake();
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            _pausedUntil = null;
            _syncNow = true;
        }

        Wake();
    }

    /// <summary>A local change at <paramref name="fullPath"/> (from the watcher or a test).</summary>
    public void NotifyLocalChange(string fullPath)
    {
        var relative = Path.GetRelativePath(Pair.LocalRoot, fullPath);
        var first = relative.Split(Path.DirectorySeparatorChar, 2)[0];
        if (relative.StartsWith("..", StringComparison.Ordinal) ||
            string.Equals(first, StatePaths.DirName, StringComparison.OrdinalIgnoreCase) ||
            SyncFilter.IsBuiltInIgnored(Path.GetFileName(fullPath)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_echo.TryGetValue(fullPath, out var until) && now <= until)
            {
                return;
            }

            _firstChange ??= now;
            _lastChange = now;
            var key = PathKey.FromPath(relative.Replace(Path.DirectorySeparatorChar, '/'));
            _refresh.Add(key);
            _refresh.Add(PathKey.Parent(key));
        }

        Wake();
    }

    /// <summary>Executes the approved pending ops (guard trips need <paramref name="acceptGuards"/>). Null: nothing ran.</summary>
    public Task<ExecutionResult?> ApproveAsync(IReadOnlyCollection<SyncOp> ops, bool acceptGuards, CancellationToken ct = default) =>
        Enqueue(async stop =>
        {
            var pending = Pending ?? throw new InvalidOperationException("Nothing waits for approval.");
            if (Pair.Mode == SyncMode.DryRun)
            {
                throw new InvalidOperationException("The pair is in DryRun mode: nothing is executed.");
            }

            if (pending.GuardTripped && !acceptGuards)
            {
                throw new InvalidOperationException("A safety guard tripped: approving needs an explicit confirmation.");
            }

            if (DateTime.UtcNow - pending.CreatedUtc > _options.MaxPendingAge)
            {
                lock (_lock)
                {
                    _pending = null;
                    _syncNow = true;
                }

                _logger.LogInformation("Pending approval is older than {Age}; re-planning", _options.MaxPendingAge);
                return null;
            }

            var approved = ops.ToHashSet(ReferenceEqualityComparer.Instance);
            using var engine = SyncEngine.Open(Pair, _remote, _logger);
            var selected = Approval.Select(pending.Cycle.Plan, op => approved.Contains(op), Pair.MaxOpsPerCycle);
            SetStatus(PairState.Syncing, $"Executing {selected.Count(o => !o.IsBaseOnly)} approved ops");
            var result = await ExecuteAsync(engine, pending.Cycle, selected, stop).ConfigureAwait(false);
            var executed = selected.ToHashSet(ReferenceEqualityComparer.Instance);
            UpdatePending(pending, pending.Ops.Where(o => !executed.Contains(o)).ToList());
            FinishCycle(result);
            return result;
        }, ct);

    /// <summary>Puts rejected pending ops into the held list (§10).</summary>
    public Task RejectAsync(IReadOnlyCollection<SyncOp> ops, CancellationToken ct = default) =>
        Enqueue(_ =>
        {
            var pending = Pending ?? throw new InvalidOperationException("Nothing waits for approval.");
            using var engine = SyncEngine.Open(Pair, _remote, _logger);
            engine.Hold(pending.Cycle, ops);
            var rejected = ops.ToHashSet(ReferenceEqualityComparer.Instance);
            UpdatePending(pending, pending.Ops.Where(o => !rejected.Contains(o)).ToList());
            PublishIdleState(failed: _status.FailedOps);
            return Task.FromResult<object?>(null);
        }, ct);

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        Wake();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    // ---------------------------------------------------------------- loop

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Func<CancellationToken, Task>? request;
            lock (_lock)
            {
                _requests.TryDequeue(out request);
            }

            if (request is not null)
            {
                await request(stop).ConfigureAwait(false);
                continue;
            }

            var now = DateTime.UtcNow;
            var (due, reason, incremental) = NextRun(now);
            if (due is { } at && at <= now)
            {
                await RunCycleAsync(reason, incremental, stop).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _wake.WaitAsync(due is { } d ? Clamp(d - now) : Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// When the next cycle is due and whether it may use the cached remote snapshot: local changes and continuations
    /// do, until the poll interval since the last full crawl ends; start, sync now, retries and polls crawl fully.
    /// </summary>
    private (DateTime? Due, string Reason, bool Incremental) NextRun(DateTime now)
    {
        lock (_lock)
        {
            var poll = (_lastFullCrawl ?? now) + Pair.RemotePollInterval;
            var cacheUsable = _cache is not null && now < poll;
            if (_paused)
            {
                if (_pausedUntil is { } until && now >= until)
                {
                    _paused = false;
                    _pausedUntil = null;
                    _syncNow = true;
                }
                else
                {
                    return (_pausedUntil, "pause ended", false);
                }
            }

            if (_syncNow)
            {
                return (now, "sync now", false);
            }

            if (_continue)
            {
                return (now, "more ops", cacheUsable);
            }

            if (_firstChange is { } first && _lastChange is { } last)
            {
                var changesDue = Min(last + Pair.LocalQuietPeriod, first + Pair.MaxBatchDelay);
                return (_retryAt is { } r && r > changesDue ? r : changesDue, "local changes", cacheUsable && _retryAt is null);
            }

            if (_retryOnlyManually)
            {
                return (null, string.Empty, false);
            }

            if (_retryAt is { } retry)
            {
                return (retry, "retry", false);
            }

            return (poll, "remote poll", false);
        }
    }

    private async Task RunCycleAsync(string reason, bool incremental, CancellationToken stop)
    {
        PlanOptions options;
        bool startup;
        lock (_lock)
        {
            startup = _startup;
            _startup = false;
            _syncNow = false;
            _continue = false;
            _firstChange = null;
            _lastChange = null;
            _retryAt = null;
            options = incremental && _cache is { } cache
                ? new PlanOptions { CachedRemote = cache, RefreshFolders = _refresh.ToList() }
                : new PlanOptions();
            _refresh.Clear();
        }

        _logger.LogInformation("Pair {Pair}: cycle ({Reason}, {Kind})", Pair.Name, reason, options.CachedRemote is null ? "full crawl" : $"{options.RefreshFolders.Count} folders re-listed");
        SetStatus(PairState.Checking, "Checking for changes");
        try
        {
            using var engine = SyncEngine.Open(Pair, _remote, _logger);
            if (startup && engine.Store.LastFullCrawlUtc is { } lastFull)
            {
                // Start-up trust (§8): after a recent full crawl the base stands in for the remote; the next full
                // crawl is due one poll interval after the last one, as if the app had kept running.
                lock (_lock)
                {
                    _lastFullCrawl = lastFull;
                }

                if (DateTime.UtcNow - lastFull < Pair.RemotePollInterval && engine.Store.InitialSyncDone)
                {
                    options = new PlanOptions { RemoteFromBase = true, RefreshFolders = [string.Empty] };
                    _logger.LogInformation("Pair {Pair}: last full crawl {At:u} is recent; trusting the base", Pair.Name, lastFull);
                }
            }

            var cycle = await engine.PlanAsync(options, new InlineProgress<string>(text => SetStatus(PairState.Checking, text)), stop).ConfigureAwait(false);
            lock (_lock)
            {
                _cache = cycle.Remote.IsComplete ? cycle.Remote : null;
                if (!cycle.RemoteFromCache)
                {
                    _lastFullCrawl = DateTime.UtcNow;
                }
            }

            if (cycle.Guards.HasErrors)
            {
                lock (_lock)
                {
                    _pending = null;
                    _lastCycleEnd = DateTime.UtcNow;
                }

                SetStatus(PairState.Error, "Stopped by a safety guard", error: string.Join(" ", cycle.Guards.Errors));
                return;
            }

            var needing = Pair.Mode == SyncMode.DryRun || cycle.Guards.RequiresApproval
                ? cycle.Plan.ActionOps.ToList()
                : Approval.NeedingApproval(cycle.Plan, Pair.Mode, cycle.UnionMode, Pair.AutoApprove).ToList();
            var needingSet = needing.ToHashSet(ReferenceEqualityComparer.Instance);
            ExecutionResult? result = null;
            var more = false;
            if (Pair.Mode != SyncMode.DryRun)
            {
                var selected = Approval.Select(cycle.Plan, op => !needingSet.Contains(op), Pair.MaxOpsPerCycle);
                if (selected.Count > 0 || cycle.Plan.Ops.Count == 0)
                {
                    if (selected.Any(o => !o.IsBaseOnly))
                    {
                        SetStatus(PairState.Syncing, $"Syncing {selected.Count(o => !o.IsBaseOnly)} items");
                    }
                    result = await ExecuteAsync(engine, cycle, selected, stop).ConfigureAwait(false);
                }

                // Ops cut by MaxOpsPerCycle: plan again right away (unless something failed).
                var runnable = cycle.Plan.ActionOps.Count(o => !needingSet.Contains(o));
                more = result is { AllDone: true } && selected.Count(o => !o.IsBaseOnly) < runnable;
            }

            lock (_lock)
            {
                _pending = needing.Count > 0 ? new PendingApproval(cycle, needing, cycle.Guards.RequiresApproval, DateTime.UtcNow) : null;
                _continue |= more;
                _offlineDelay = _options.OfflineRetryMin;
            }

            FinishCycle(result);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (RemoteException ex) when (ex.Kind == RemoteErrorKind.Auth)
        {
            Fail(PairState.Error, "Error: authentication failed (check the app password)", ex, manualOnly: true);
        }
        catch (RemoteException ex) when (ex.Kind is RemoteErrorKind.Offline or RemoteErrorKind.Transient)
        {
            TimeSpan delay;
            lock (_lock)
            {
                delay = _offlineDelay;
                _offlineDelay = Min(_offlineDelay * 2, _options.OfflineRetryMax);
            }

            Fail(PairState.Offline, $"Offline, retrying in {Format(delay)}", ex, retryIn: delay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else (root sanity, state DB, disk): keep the loop alive and retry at the next poll.
            Fail(PairState.Error, "Error: " + ex.Message, ex, retryIn: Pair.RemotePollInterval);
        }
    }

    private async Task<ExecutionResult> ExecuteAsync(SyncEngine engine, CyclePlan cycle, IReadOnlyList<SyncOp> ops, CancellationToken stop)
    {
        var done = 0;
        var total = ops.Count(o => !o.IsBaseOnly);

        // Mark before executing: the watcher may report a moved-in file before the op reports completion.
        // The remote folders our ops touch are stale in the cached snapshot: re-list them next time.
        foreach (var op in ops.Where(o => !o.IsBaseOnly))
        {
            MarkEcho(op);
            lock (_lock)
            {
                foreach (var key in new[] { op.Key, op.NewKey })
                {
                    if (key is not null)
                    {
                        _refresh.Add(key);
                        _refresh.Add(PathKey.Parent(key));
                    }
                }
            }
        }

        var result = await engine.ExecuteAsync(cycle, ops, new InlineProgress<OpResult>(r =>
        {
            if (!r.Op.IsBaseOnly)
            {
                SetStatus(PairState.Syncing, $"Syncing {++done} of {total}");
                OpCompleted?.Invoke(this, r);
            }
        }), stop).ConfigureAwait(false);

        // Our own changes arrive from the watcher after the ops; ignore them for a while (§8).
        var until = DateTime.UtcNow + _options.EchoWindow;
        lock (_lock)
        {
            foreach (var key in _echo.Keys.ToList())
            {
                _echo[key] = until;
            }
        }

        return result;
    }

    private void MarkEcho(SyncOp op)
    {
        var until = DateTime.UtcNow + _options.EchoWindow;
        lock (_lock)
        {
            foreach (var path in new[] { op.LocalPath, op.NewLocalPath })
            {
                if (path is not null)
                {
                    var full = Model.PathKey.ToLocalFull(Pair.LocalRoot, path);
                    _echo[full] = until;
                    _echo[Path.GetDirectoryName(full)!] = until;
                }
            }
        }
    }

    private void FinishCycle(ExecutionResult? result)
    {
        lock (_lock)
        {
            _lastCycleEnd = DateTime.UtcNow;
            var now = DateTime.UtcNow;
            foreach (var expired in _echo.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
            {
                _echo.Remove(expired);
            }
        }

        if (result?.AbortReason is { } abort)
        {
            _logger.LogWarning("Pair {Pair}: cycle aborted: {Reason}", Pair.Name, abort);
        }

        PublishIdleState(result?.Failed ?? 0, DateTime.UtcNow);
    }

    private void PublishIdleState(int failed, DateTime? syncedUtc = null)
    {
        var pending = Pending;
        if (syncedUtc is not null)
        {
            lock (_lock)
            {
                _status = _status with { LastSyncUtc = syncedUtc };
            }
        }

        if (pending is { Ops.Count: > 0 })
        {
            var text = Pair.Mode == SyncMode.DryRun
                ? $"Dry run: {pending.Ops.Count} ops planned"
                : $"{pending.Ops.Count} items need approval" + (pending.GuardTripped ? " (safety guard)" : string.Empty);
            SetStatus(PairState.NeedsAttention, text, failed: failed);
        }
        else if (failed > 0)
        {
            SetStatus(PairState.NeedsAttention, $"{failed} items failed (see the log)", failed: failed);
        }
        else
        {
            SetStatus(PairState.Idle, "Up to date", failed: 0);
        }
    }

    private void UpdatePending(PendingApproval pending, IReadOnlyList<SyncOp> remaining)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = remaining.Count > 0 ? pending with { Ops = remaining } : null;
            }
        }
    }

    private void Fail(PairState state, string text, Exception ex, TimeSpan? retryIn = null, bool manualOnly = false)
    {
        _logger.LogWarning(ex, "Pair {Pair}: cycle failed", Pair.Name);
        lock (_lock)
        {
            _cache = null;
            _lastCycleEnd = DateTime.UtcNow;
            _retryAt = retryIn is { } r ? DateTime.UtcNow + r : null;
            _retryOnlyManually = manualOnly;
        }

        SetStatus(state, text, error: ex.Message);
    }

    private void SetStatus(PairState state, string text, int? failed = null, string? error = null)
    {
        PairStatus status;
        lock (_lock)
        {
            if (_paused && state is not (PairState.Syncing or PairState.Checking or PairState.Paused))
            {
                state = PairState.Paused;
                text = PausedText();
            }

            var (next, _) = state is PairState.Syncing or PairState.Checking ? (null, string.Empty) : NextRunNoSideEffects();
            status = _status = _status with
            {
                State = state,
                Text = text,
                NextRunUtc = next,
                PausedUntilUtc = _paused ? _pausedUntil : null,
                PendingApprovals = _pending?.Ops.Count ?? 0,
                FailedOps = failed ?? _status.FailedOps,
                Error = state is PairState.Error or PairState.Offline ? error : null,
            };
        }

        StatusChanged?.Invoke(this, status);
    }

    private (DateTime?, string) NextRunNoSideEffects()
    {
        if (_paused)
        {
            return (_pausedUntil, string.Empty);
        }

        if (_retryOnlyManually)
        {
            return (null, string.Empty);
        }

        return (_retryAt ?? (_lastFullCrawl ?? DateTime.UtcNow) + Pair.RemotePollInterval, string.Empty);
    }

    private string PausedText() =>
        _pausedUntil is { } until ? $"Paused until {until.ToLocalTime():HH:mm}" : "Paused";

    private Task<T> Enqueue<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        lock (_lock)
        {
            _requests.Enqueue(async stop =>
            {
                try
                {
                    tcs.TrySetResult(await work(stop).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    await registration.DisposeAsync().ConfigureAwait(false);
                }
            });
        }

        Wake();
        return tcs.Task;
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(Pair.LocalRoot)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => NotifyLocalChange(e.FullPath);
        _watcher.Changed += (_, e) => NotifyLocalChange(e.FullPath);
        _watcher.Deleted += (_, e) => NotifyLocalChange(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            NotifyLocalChange(e.OldFullPath);
            NotifyLocalChange(e.FullPath);
        };

        // Buffer overflow: events were lost; every cycle scans the whole tree, so one more cycle covers them.
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "Pair {Pair}: watcher error; scheduling a cycle", Pair.Name);
            lock (_lock)
            {
                _firstChange ??= DateTime.UtcNow;
                _lastChange = DateTime.UtcNow;
                _cache = null;
            }

            Wake();
        };
        _watcher.EnableRaisingEvents = true;
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.Zero ? TimeSpan.Zero : t > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : t;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";
}
