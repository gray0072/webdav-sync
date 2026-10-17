using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;
using WebDavSync.Core.State;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Background pair service (§8, §10, §14): triggers, approvals, pause, errors.</summary>
public sealed class PairServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly TempDir _local = new();
    private readonly InMemoryRemoteStore _remote = new();
    private readonly PairSettings _pair;
    private PairService? _service;

    public PairServiceTests()
    {
        _pair = new PairSettings
        {
            Name = "test",
            LocalRoot = _local.Path,
            RemoteUrl = "https://example.invalid/",
            RemoteRoot = "/test",
            Mode = SyncMode.Auto,
            LocalDeleteTarget = LocalDeleteTarget.StateTrash,
            LocalQuietPeriod = TimeSpan.FromMilliseconds(50),
            MaxBatchDelay = TimeSpan.FromMilliseconds(500),
        };
        PairSetup.InitializeLocal(_pair, resetState: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }

        _local.Dispose();
    }

    [Fact]
    public async Task Initial_sync_waits_for_approval_even_in_auto_mode_then_runs_by_itself()
    {
        _remote.AddFile("/test/a.txt", "alpha");
        var service = Start();

        var status = await WaitAsync(service, s => s.State == PairState.NeedsAttention);
        Assert.Equal(1, status.PendingApprovals);
        var pending = service.Pending!;
        Assert.True(pending.Cycle.UnionMode);

        var result = await service.ApproveAsync(pending.Ops, acceptGuards: false, TestContext.Current.CancellationToken);
        Assert.True(result!.Done > 0);
        Assert.Equal("alpha", File.ReadAllText(_local.Full("a.txt")));
        Assert.Null(service.Pending);

        // The next cycle has an empty plan: initial sync done; later changes run without approval.
        await SyncNowAndWaitAsync(service);
        _remote.AddFile("/test/b.txt", "beta");
        await SyncNowAndWaitAsync(service);
        Assert.Equal("beta", File.ReadAllText(_local.Full("b.txt")));
        Assert.Equal(PairState.Idle, service.Status.State);
    }

    [Fact]
    public async Task Local_change_runs_a_cycle_after_the_quiet_period()
    {
        var service = await StartInSyncAsync();
        var path = _local.File("new.txt", "fresh");
        var before = service.Status.LastSyncUtc;

        service.NotifyLocalChange(path);

        await WaitAsync(service, s => s.State == PairState.Idle && s.LastSyncUtc > before);
        Assert.Equal("fresh", _remote.Read("/test/new.txt"));
    }

    [Fact]
    public async Task Watcher_picks_up_a_new_file_and_ignores_our_own_download()
    {
        var service = await StartInSyncAsync(watch: true);
        var before = service.Status.LastSyncUtc;
        _local.File("watched.txt", "seen");

        await WaitAsync(service, s => s.State == PairState.Idle && s.LastSyncUtc > before);
        Assert.Equal("seen", _remote.Read("/test/watched.txt"));

        _remote.AddFile("/test/down.txt", "from remote");
        var cycles = 0;
        service.StatusChanged += (_, s) =>
        {
            if (s.Text == "Checking for changes")
            {
                Interlocked.Increment(ref cycles);
            }
        };
        await SyncNowAndWaitAsync(service);
        Assert.Equal("from remote", File.ReadAllText(_local.Full("down.txt")));
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref cycles));
    }

    [Fact]
    public async Task Rejected_ops_are_held_and_not_proposed_again()
    {
        var service = await StartInSyncAsync();
        _pair.Mode = SyncMode.ApproveAll;
        _remote.AddFile("/test/unwanted.bin", "junk");

        await SyncNowAndWaitAsync(service, PairState.NeedsAttention);
        await service.RejectAsync(service.Pending!.Ops, TestContext.Current.CancellationToken);
        Assert.Null(service.Pending);

        await SyncNowAndWaitAsync(service);
        Assert.Null(service.Pending);
        Assert.False(File.Exists(_local.Full("unwanted.bin")));
        using var store = StateStore.Open(new StatePaths(_local.Path).StateDb);
        Assert.Equal("unwanted.bin", Assert.Single(store.LoadHeld()).Key);
    }

    [Fact]
    public async Task Auto_approve_rule_runs_matching_ops_and_leaves_the_rest_pending()
    {
        var service = await StartInSyncAsync();
        _pair.Mode = SyncMode.ApproveAll;
        _pair.AutoApprove.Add(new AutoApproveRule { Type = OpType.Download, Folder = "Camera Uploads" });
        _remote.AddFile("/test/Camera Uploads/1.jpg", "photo");
        _remote.AddFile("/test/Docs/2.txt", "doc");

        await SyncNowAndWaitAsync(service, PairState.NeedsAttention);

        Assert.Equal("photo", File.ReadAllText(_local.Full("Camera Uploads/1.jpg")));
        Assert.False(File.Exists(_local.Full("Docs/2.txt")));
        Assert.DoesNotContain(service.Pending!.Ops, o => o.Key.StartsWith("camera uploads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paused_pair_does_not_sync_until_resumed()
    {
        var service = await StartInSyncAsync();
        service.Pause(null);
        _remote.AddFile("/test/later.txt", "later");
        service.SyncNow();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(PairState.Paused, service.Status.State);
        Assert.False(File.Exists(_local.Full("later.txt")));

        var before = service.Status.LastSyncUtc;
        service.Resume();
        await WaitAsync(service, s => s.State == PairState.Idle && s.LastSyncUtc > before);
        Assert.Equal("later", File.ReadAllText(_local.Full("later.txt")));
    }

    [Fact]
    public async Task Auth_failure_stops_until_sync_now()
    {
        var failing = new AuthFailingStore(_remote);
        _service = new PairService(_pair, failing, NullLogger.Instance, new PairServiceOptions { Watch = false });
        _service.Start();

        var status = await WaitAsync(_service, s => s.State == PairState.Error);
        Assert.Contains("authentication", status.Text, StringComparison.Ordinal);
        Assert.Null(status.NextRunUtc);

        failing.Fail = false;
        _service.SyncNow();
        await WaitAsync(_service, s => s.State is PairState.NeedsAttention or PairState.Idle);
    }

    private PairService Start(bool watch = false)
    {
        _service = new PairService(_pair, _remote, NullLogger.Instance, new PairServiceOptions { Watch = watch, EchoWindow = TimeSpan.FromSeconds(5) });
        _service.Start();
        return _service;
    }

    /// <summary>A service whose pair finished the initial sync (one local file).</summary>
    private async Task<PairService> StartInSyncAsync(bool watch = false)
    {
        _local.File("base.txt", "base");
        var service = Start(watch);
        await WaitAsync(service, s => s.State == PairState.NeedsAttention);
        await service.ApproveAsync(service.Pending!.Ops, acceptGuards: false, TestContext.Current.CancellationToken);
        await SyncNowAndWaitAsync(service);
        return service;
    }

    private static async Task SyncNowAndWaitAsync(PairService service, PairState expected = PairState.Idle)
    {
        var before = service.Status.LastSyncUtc;
        service.SyncNow();
        await WaitAsync(service, s => s.State == expected && s.LastSyncUtc != before);
    }

    private static async Task<PairStatus> WaitAsync(PairService service, Func<PairStatus, bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = service.Status;
            if (condition(status))
            {
                return status;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Status condition not reached; last status: {service.Status}");
    }

    private sealed class AuthFailingStore(InMemoryRemoteStore inner) : IRemoteStore
    {
        public volatile bool Fail = true;

        public Task<RemoteListing> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);

        public Task<RemoteItem?> StatAsync(string path, CancellationToken ct) =>
            Fail ? throw new RemoteException(RemoteErrorKind.Auth, "401", HttpStatusCode.Unauthorized) : inner.StatAsync(path, ct);

        public Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct) => inner.GetAsync(path, offset, ct);

        public Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct) => inner.PutAsync(path, content, length, mtimeUtc, ct);

        public Task MkdirAsync(string path, CancellationToken ct) => inner.MkdirAsync(path, ct);

        public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => inner.MoveAsync(from, to, overwrite, ct);

        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct) => inner.GetQuotaAsync(ct);
    }
}
