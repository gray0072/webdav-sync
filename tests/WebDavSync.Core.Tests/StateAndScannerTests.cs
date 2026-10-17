using Microsoft.Extensions.Logging.Abstractions;
using WebDavSync.Core.Config;
using WebDavSync.Core.Local;
using WebDavSync.Core.Model;
using WebDavSync.Core.Remote;
using WebDavSync.Core.State;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

public sealed class StateStoreTests
{
    private static BaseEntry Entry(string path, bool dir = false) =>
        new(PathKey.FromPath(path), dir, path, path, 42, dir ? 0 : 3, dir ? null : World.T0, dir ? null : World.Hash("abc"), "e1", 3, World.T0, null, true, World.T0);

    [Fact]
    public void Base_round_trips_and_rebases_like_the_planner()
    {
        using var temp = new TempDir();
        using var store = StateStore.Open(Path.Combine(temp.Path, "state.db"));
        store.ApplyBase([new UpsertBase(Entry("Docs", true)), new UpsertBase(Entry("Docs/a.txt")), new UpsertBase(Entry("Docsx/b.txt"))]);
        var loaded = store.LoadBase();
        Assert.Equal(Entry("Docs/a.txt"), loaded["docs/a.txt"]);

        store.ApplyBase([new RebaseSubtree("docs", "new/place", "New/Place", "new/place")]);
        var moved = store.LoadBase();
        Assert.Equal(["docsx/b.txt", "new/place", "new/place/a.txt"], moved.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("New/Place/a.txt", moved["new/place/a.txt"].LocalPath);
        Assert.Equal("new/place/a.txt", moved["new/place/a.txt"].RemotePath);

        store.ApplyBase([new RemoveBaseSubtree("new/place")]);
        Assert.Equal(["docsx/b.txt"], store.LoadBase().Keys);
    }

    [Fact]
    public void Journal_recovery_marks_running_ops_interrupted()
    {
        using var temp = new TempDir();
        using var store = StateStore.Open(Path.Combine(temp.Path, "state.db"));
        store.JournalStart("op1", "c1", "Upload", "a", null);
        store.JournalStart("op2", "c1", "Upload", "b", null);
        store.JournalFinish("op2", JournalStatus.Done, 1, null, [new UpsertBase(Entry("b"))]);
        Assert.Equal(1, store.RecoverJournal());
        Assert.Equal(1, store.BaseCount);
    }

    [Fact]
    public void Daily_backup_keeps_a_copy()
    {
        using var temp = new TempDir();
        using var store = StateStore.Open(Path.Combine(temp.Path, "state.db"));
        store.InitialSyncDone = true;
        store.BackupDaily(Path.Combine(temp.Path, "backup"));
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "backup"), "state-*.db"));
    }
}

public sealed class LocalScannerTests
{
    [Fact]
    public async Task Scans_with_file_ids_hashes_and_filters()
    {
        using var temp = new TempDir();
        temp.File("a.txt", "hello");
        temp.File("Docs/b.txt", "content B");
        temp.File("Docs/desktop.ini", "junk");
        temp.File(".cloud.db", "legacy");
        temp.File(".webdavsync/state.db", "state");
        temp.File("Photos/p.jpg", "jpg");
        temp.Dir("Empty");

        var snapshot = await new LocalScanner(new SyncFilter(["Photos"]), NullLogger.Instance)
            .ScanAsync(temp.Path, new Dictionary<string, BaseEntry>(), LocalHashMode.All, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["a.txt", "docs", "docs/b.txt", "empty"], snapshot.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("photos", snapshot.SkippedKeys);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(World.Hash("hello"), snapshot.Entries["a.txt"].Hash);
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(snapshot.Entries["a.txt"].FileId);
            Assert.NotNull(snapshot.Entries["docs"].FileId);
        }
    }

    [Fact]
    public async Task Reuses_base_hash_when_unchanged_and_hashes_only_what_is_needed()
    {
        using var temp = new TempDir();
        temp.File("cached.txt", "real content", World.T0);
        temp.File("new.txt", "new");
        temp.File("wanted.txt", "wanted");
        var fake = ContentHash.Parse(new string('A', 40));
        var baseEntries = new Dictionary<string, BaseEntry>
        {
            ["cached.txt"] = new("cached.txt", false, "cached.txt", "cached.txt", null, 12, World.T0, fake, null, 12, World.T0, fake, true, World.T0),
        };

        var snapshot = await new LocalScanner(SyncFilter.Empty, NullLogger.Instance)
            .ScanAsync(temp.Path, baseEntries, LocalHashMode.Needed, key => key == "wanted.txt", ct: TestContext.Current.CancellationToken);

        Assert.Equal(fake, snapshot.Entries["cached.txt"].Hash);
        Assert.Null(snapshot.Entries["new.txt"].Hash);
        Assert.Equal(World.Hash("wanted"), snapshot.Entries["wanted.txt"].Hash);
    }
}

public sealed class FileIdTests
{
    [Fact]
    public void File_id_survives_rename_and_is_not_reused_for_a_new_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS file ids");
        using var temp = new TempDir();
        var a = temp.File("a.txt", "a");
        var b = temp.File("b.txt", "b");
        var idA = LocalFs.GetFileId(a);
        Assert.NotNull(idA);
        Assert.NotEqual(idA, LocalFs.GetFileId(b));

        File.Move(a, temp.Full("renamed.txt"));
        Assert.Equal(idA, LocalFs.GetFileId(temp.Full("renamed.txt")));

        // The MFT record may be reused, but the sequence number (high 16 bits) changes.
        File.Delete(temp.Full("renamed.txt"));
        var c = temp.File("c.txt", "c");
        Assert.NotEqual(idA, LocalFs.GetFileId(c));
        Assert.Equal(LocalFs.GetVolumeSerial(c), LocalFs.GetVolumeSerial(b));
        Assert.NotEqual(0u, LocalFs.GetVolumeSerial(b));
    }
}

public sealed class RemoteScannerTests
{
    [Fact]
    public async Task Crawl_skips_invalid_names_and_marks_failed_folders_unlisted()
    {
        var store = new InMemoryRemoteStore();
        store.AddFile("/root/ok.txt", "ok");
        store.AddFile("/root/bad:name.txt", "bad");
        store.AddFile("/root/broken/x.txt", "x");
        store.AddFile("/root/.webdavsync/pair.json", "{}");
        store.ListFault = path => path.EndsWith("/broken", StringComparison.Ordinal) ? new RemoteException(RemoteErrorKind.Transient, "boom") : null;

        var snapshot = await new RemoteScanner(store, SyncFilter.Empty, NullLogger.Instance).CrawlAsync("/root", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["broken", "ok.txt"], snapshot.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("bad:name.txt", snapshot.SkippedKeys);
        Assert.Contains(string.Empty, snapshot.ListedFolders);
        Assert.DoesNotContain("broken", snapshot.ListedFolders);
        Assert.False(snapshot.IsComplete);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Auth_failure_aborts_the_crawl(int parallel)
    {
        var store = new InMemoryRemoteStore();
        store.AddDir("/root/a");
        store.AddDir("/root/b/c");
        store.ListFault = path => path.EndsWith("/a", StringComparison.Ordinal) ? new RemoteException(RemoteErrorKind.Auth, "401") : null;
        var slow = new SlowStore(store);
        await Assert.ThrowsAsync<RemoteException>(() => new RemoteScanner(slow, SyncFilter.Empty, NullLogger.Instance, parallel).CrawlAsync("/root", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parallel_crawl_overlaps_listings_and_matches_the_sequential_snapshot()
    {
        var store = new InMemoryRemoteStore();
        for (var i = 0; i < 6; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                store.AddFile($"/root/d{i}/s{j}/f.txt", $"{i}{j}");
            }
        }

        store.AddFile("/root/d2/broken/x.txt", "x");
        store.ListFault = path => path.EndsWith("/broken", StringComparison.Ordinal) ? new RemoteException(RemoteErrorKind.Transient, "boom") : null;
        var ct = TestContext.Current.CancellationToken;

        var sequential = await new RemoteScanner(store, SyncFilter.Empty, NullLogger.Instance).CrawlAsync("/root", ct: ct);
        var slow = new SlowStore(store);
        var parallel = await new RemoteScanner(slow, SyncFilter.Empty, NullLogger.Instance, parallel: 4).CrawlAsync("/root", ct: ct);

        Assert.Equal(sequential.Entries.Keys.Order(StringComparer.Ordinal), parallel.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(sequential.ListedFolders.Order(StringComparer.Ordinal), parallel.ListedFolders.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("d2/broken", parallel.ListedFolders);
        Assert.InRange(slow.MaxInFlight, 2, 4);
    }

    /// <summary>Adds latency to listings and records how many were in flight at once.</summary>
    private sealed class SlowStore(InMemoryRemoteStore inner) : IRemoteStore
    {
        private readonly Lock _lock = new();
        private int _inFlight;

        public int MaxInFlight { get; private set; }

        public async Task<RemoteListing> ListAsync(string path, CancellationToken ct)
        {
            lock (_lock)
            {
                MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
            }

            try
            {
                await Task.Delay(20, ct);
                lock (_lock)
                {
                    return inner.ListAsync(path, ct).GetAwaiter().GetResult();
                }
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                }
            }
        }

        public Task<RemoteItem?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct) => inner.GetAsync(path, offset, ct);

        public Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct) => inner.PutAsync(path, content, length, mtimeUtc, ct);

        public Task MkdirAsync(string path, CancellationToken ct) => inner.MkdirAsync(path, ct);

        public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => inner.MoveAsync(from, to, overwrite, ct);

        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct) => inner.GetQuotaAsync(ct);
    }
}
