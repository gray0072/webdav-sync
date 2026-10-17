using Microsoft.Extensions.Logging.Abstractions;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Execution;
using WebDavSync.Core.Local;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;
using WebDavSync.Core.State;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>End-to-end cycles over temp folders and an in-memory WebDAV store: scenarios A–E (§7) and executor rules (§6.6).</summary>
public sealed class EngineScenarioTests : IDisposable
{
    private const string Root = "/test";
    private readonly TempDir _local = new();
    private readonly InMemoryRemoteStore _remote = new();
    private readonly PairSettings _pair;

    public EngineScenarioTests()
    {
        _pair = new PairSettings
        {
            Name = "test",
            LocalRoot = _local.Path,
            RemoteUrl = "https://example.invalid/",
            RemoteRoot = Root,
            LocalDeleteTarget = LocalDeleteTarget.StateTrash,
            MassDeleteCount = 1000,
            MassDeletePercent = 100,
        };
        PairSetup.InitializeLocal(_pair, resetState: false);
    }

    public void Dispose() => _local.Dispose();

    [Fact]
    public async Task Scenario_A_cloud_to_empty_local_folder()
    {
        _remote.AddFile("/test/a.txt", "alpha");
        _remote.AddFile("/test/Docs/Отчёт #1.txt", "report");
        _remote.AddDir("/test/Empty");

        var (cycle, result) = await SyncAsync();

        Assert.True(cycle.UnionMode);
        Assert.True(result.AllDone);
        Assert.DoesNotContain(cycle.Plan.Ops, o => o.IsDelete);
        AssertInSync();
        Assert.True(Directory.Exists(_local.Full("Empty")));
        Assert.True(_remote.Exists("/test/.webdavsync/pair.json"));
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Scenario_B_cloud_to_existing_local_folder_is_a_union_merge()
    {
        _remote.AddFile("/test/same.txt", "identical");
        _remote.AddFile("/test/diff.txt", "remote version");
        _remote.AddFile("/test/remote-only.txt", "r");
        _local.File("same.txt", "identical");
        _local.File("diff.txt", "local version, longer");
        _local.File("local-only.txt", "l");

        var (cycle, result) = await SyncAsync();

        Assert.True(result.AllDone);
        Assert.Equal(1, cycle.Plan.Count(OpType.Compare));
        Assert.Equal(1, cycle.Plan.Count(OpType.Conflict));
        AssertInSync();
        var files = _local.Files();
        Assert.Equal("local version, longer", files["diff.txt"]);
        Assert.Equal("remote version", Assert.Single(files, f => f.Key.StartsWith("diff (conflict ", StringComparison.Ordinal) && f.Key.EndsWith(" cloud).txt", StringComparison.Ordinal)).Value);
        Assert.Equal(5, files.Count);
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Scenario_C_local_folder_to_missing_cloud_root()
    {
        _local.File("a.txt", "alpha");
        _local.File("Deep/Er/b.txt", "beta");
        _local.Dir("EmptyDir");

        var (_, result) = await SyncAsync();

        Assert.True(result.AllDone);
        AssertInSync();
        Assert.True(_remote.Exists("/test/EmptyDir"));
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Scenario_E_normal_changes_on_both_sides()
    {
        _local.File("edit-local.txt", "v1");
        _local.File("edit-remote.txt", "v1");
        _local.File("delete-local.txt", "d");
        _local.File("delete-remote.txt", "d");
        _local.File("rename-me.txt", "r");
        _local.File("Folder/inner.txt", "i");
        await SyncAsync();
        AssertInSync();

        File.WriteAllText(_local.Full("edit-local.txt"), "v2 local");
        _remote.AddFile("/test/edit-remote.txt", "v2 remote");
        File.Delete(_local.Full("delete-local.txt"));
        await _remote.DeleteAsync("/test/delete-remote.txt", CancellationToken.None);
        File.Move(_local.Full("rename-me.txt"), _local.Full("renamed.txt"));
        _local.File("new-local.txt", "n");

        var (cycle, result) = await SyncAsync();

        Assert.True(result.AllDone, string.Join("\n", result.Results.Select(r => $"{r.Op} {r.Status} {r.Message}")));
        Assert.True(cycle.Plan.Count(OpType.MoveRemote) == 1, PlanAssert.Dump(cycle.Plan));
        AssertInSync();
        Assert.Equal("v2 local", _remote.Read("/test/edit-local.txt"));
        Assert.Equal("v2 remote", _local.Files()["edit-remote.txt"]);
        Assert.False(File.Exists(_local.Full("delete-remote.txt")));
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(_local.Path, ".webdavsync", "trash"), "*", SearchOption.AllDirectories), f => f.EndsWith("delete-remote.txt", StringComparison.Ordinal));
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(_local.Path, ".webdavsync", "trash"), "*", SearchOption.AllDirectories), f => f.EndsWith("edit-remote.txt", StringComparison.Ordinal));
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Conflicting_edits_keep_both_versions_on_both_sides()
    {
        _local.File("a.txt", "base");
        await SyncAsync();
        File.WriteAllText(_local.Full("a.txt"), "local edit");
        _remote.AddFile("/test/a.txt", "remote edit, longer");

        var (_, result) = await SyncAsync();

        Assert.True(result.AllDone);
        AssertInSync();
        var files = _local.Files();
        Assert.Equal("local edit", files["a.txt"]);
        Assert.Equal("remote edit, longer", files.Single(f => f.Key.Contains("(conflict", StringComparison.Ordinal)).Value);
        using var store = StateStore.Open(Path.Combine(_local.Path, ".webdavsync", "state.db"));
        Assert.Equal(1, store.OpenConflictCount);
    }

    [Fact]
    public async Task Edge17_Folder_deleted_locally_while_a_file_was_added_remotely()
    {
        _local.File("Docs/old.txt", "old");
        await SyncAsync();
        Directory.Delete(_local.Full("Docs"), recursive: true);
        _remote.AddFile("/test/Docs/new.txt", "added remotely");

        var (_, result) = await SyncAsync();

        Assert.True(result.AllDone);
        AssertInSync();
        Assert.Equal(["Docs/new.txt"], _local.Files().Keys);
    }

    [Fact]
    public async Task Edge16_Local_folder_rename_is_a_single_remote_move()
    {
        for (var i = 0; i < 20; i++)
        {
            _local.File($"Old/f{i}.txt", $"c{i}");
        }

        await SyncAsync();
        Directory.Move(_local.Full("Old"), _local.Full("New"));
        var before = _remote.Requests;

        var (cycle, result) = await SyncAsync();

        Assert.True(result.AllDone);
        Assert.Equal([OpType.MoveRemote], cycle.Plan.ActionOps.Select(o => o.Type));
        AssertInSync();
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Remote_folder_rename_is_a_single_local_move()
    {
        for (var i = 0; i < 5; i++)
        {
            _local.File($"Old/f{i}.txt", $"c{i}");
        }

        await SyncAsync();
        await _remote.MoveAsync("/test/Old", "/test/New", false, CancellationToken.None);

        var (cycle, result) = await SyncAsync();

        Assert.True(result.AllDone);
        Assert.Equal([OpType.MoveLocal], cycle.Plan.ActionOps.Select(o => o.Type));
        AssertInSync();
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Local_change_after_planning_skips_the_op()
    {
        _local.File("a.txt", "v1");
        await SyncAsync();
        _remote.AddFile("/test/a.txt", "remote v2");

        using (var engine = SyncEngine.Open(_pair, _remote, NullLogger.Instance))
        {
            var cycle = await engine.PlanAsync(ct: TestContext.Current.CancellationToken);
            File.WriteAllText(_local.Full("a.txt"), "local edit after planning");
            var result = await engine.ExecuteAsync(cycle, cycle.Plan.Ops, ct: TestContext.Current.CancellationToken);
            Assert.Equal(JournalStatus.Skipped, Assert.Single(result.Results).Status);
        }

        Assert.Equal("local edit after planning", _local.Files()["a.txt"]);
    }

    [Fact]
    public async Task Edge38_Missing_remote_marker_after_initial_sync_stops_the_pair()
    {
        _local.File("a.txt", "a");
        await SyncAsync();
        await _remote.DeleteAsync("/test/.webdavsync", CancellationToken.None);

        using var engine = SyncEngine.Open(_pair, _remote, NullLogger.Instance);
        await Assert.ThrowsAsync<PairStoppedException>(() => engine.PlanAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Edge30_Missing_remote_root_after_initial_sync_stops_the_pair()
    {
        _local.File("a.txt", "a");
        await SyncAsync();
        await _remote.DeleteAsync(Root, CancellationToken.None);

        using var engine = SyncEngine.Open(_pair, _remote, NullLogger.Instance);
        await Assert.ThrowsAsync<PairStoppedException>(() => engine.PlanAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Edge31_Second_engine_on_the_same_pair_is_refused()
    {
        using var first = SyncEngine.Open(_pair, _remote, NullLogger.Instance);
        Assert.Throws<PairStoppedException>(() => SyncEngine.Open(_pair, _remote, NullLogger.Instance));
    }

    [Fact]
    public void Edge39_Copied_folder_with_another_pair_id_is_refused()
    {
        var other = new PairSettings { LocalRoot = _local.Path, RemoteRoot = Root };
        Assert.Throws<PairStoppedException>(() => SyncEngine.Open(other, _remote, NullLogger.Instance));
        Assert.Throws<PairStoppedException>(() => PairSetup.InitializeLocal(other, resetState: false));
    }

    [Fact]
    public async Task Incomplete_crawl_keeps_union_mode()
    {
        _remote.AddFile("/test/ok/a.txt", "a");
        _remote.AddFile("/test/broken/b.txt", "b");
        _remote.ListFault = p => p.EndsWith("/broken", StringComparison.Ordinal) ? new Remote.RemoteException(Remote.RemoteErrorKind.Transient, "boom") : null;

        var (cycle, result) = await SyncAsync();

        Assert.True(result.AllDone);
        Assert.False(cycle.Remote.IsComplete);
        using var store = StateStore.Open(Path.Combine(_local.Path, ".webdavsync", "state.db"));
        Assert.False(store.InitialSyncDone);
    }

    [Fact]
    public async Task Scenario_D_resume_from_the_legacy_client()
    {
        var t = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var seconds = (long)(t - DateTime.UnixEpoch).TotalSeconds;
        var fixture = new LegacyFixture();
        var lDocs = fixture.Folder(fixture.LocalRoot, "Docs");
        var rDocs = fixture.Folder(fixture.RemoteRoot, "Docs", tree: 2);
        foreach (var (name, content) in new[] { ("keep.txt", "keep me"), ("deleted-since.txt", "gone"), ("edited-since.txt", "old text") })
        {
            var hash = ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(content)).Hex;
            fixture.File(lDocs, name, content.Length, hash, seconds);
            fixture.File(rDocs, name, content.Length, hash, seconds - 100);
            _remote.AddFile("/test/Docs/" + name, content);
            _local.File("Docs/" + name, content, t);
        }

        fixture.Write(_local.Full(".cloud.db"));
        File.Delete(_local.Full("Docs/deleted-since.txt"));
        File.WriteAllText(_local.Full("Docs/edited-since.txt"), "new text, edited after the old client stopped");

        var import = PairSetup.ImportLegacy(_pair, force: false);
        Assert.Equal(4, import.Base.Count);

        var (cycle, result) = await SyncAsync();

        Assert.False(cycle.UnionMode);
        PlanAssert.Only(cycle.Plan,
            (OpType.UpdateBase, "docs"),
            (OpType.UpdateBase, "docs/keep.txt"),
            (OpType.Upload, "docs/edited-since.txt"),
            (OpType.DeleteRemote, "docs/deleted-since.txt"));
        Assert.True(result.AllDone);
        AssertInSync();
        Assert.True(File.Exists(_local.Full(".cloud.db")));
        Assert.False(_remote.Exists("/test/.cloud.db"));
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Keep_local_overwrites_a_broken_remote_version()
    {
        _local.File("db.kdbx", "good v1");
        await SyncAsync();
        _local.File("db.kdbx", "good v2, edited locally");
        _remote.AddFile("/test/db.kdbx", "broken by another tool");

        var (cycle, result) = await SyncAsync(new Dictionary<string, Side> { ["db.kdbx"] = Side.Local });

        PlanAssert.Only(cycle.Plan, (OpType.Upload, "db.kdbx"));
        Assert.True(result.AllDone);
        Assert.Equal("good v2, edited locally", _remote.Files(Root)["db.kdbx"]);
        AssertInSync();
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Held_op_is_not_proposed_until_the_item_changes()
    {
        _local.File("keep.txt", "k");
        await SyncAsync();
        _remote.AddFile("/test/unwanted.bin", "remote junk");
        _remote.AddFile("/test/wanted.txt", "w");

        using (var engine = SyncEngine.Open(_pair, _remote, NullLogger.Instance))
        {
            var cycle = await engine.PlanAsync(new PlanOptions { Hold = ["unwanted.bin"] }, ct: TestContext.Current.CancellationToken);
            PlanAssert.Only(cycle.Plan, (OpType.Download, "wanted.txt"));
            Assert.Equal("unwanted.bin", Assert.Single(cycle.Held).Key);
            await engine.ExecuteAsync(cycle, cycle.Plan.Ops, ct: TestContext.Current.CancellationToken);
        }

        using (var engine = SyncEngine.Open(_pair, _remote, NullLogger.Instance))
        {
            var cycle = await engine.PlanAsync(ct: TestContext.Current.CancellationToken);
            PlanAssert.Empty(cycle.Plan);
            Assert.Single(cycle.Held);
            Assert.Single(engine.Store.LoadHeld());
        }

        Assert.False(File.Exists(_local.Full("unwanted.bin")));
        _remote.AddFile("/test/unwanted.bin", "remote junk, now changed");

        var (changed, result) = await SyncAsync();
        PlanAssert.Only(changed.Plan, (OpType.Download, "unwanted.bin"));
        Assert.True(result.AllDone);
        await AssertUpToDateAsync();
        using var check = SyncEngine.Open(_pair, _remote, NullLogger.Instance);
        Assert.Empty(check.Store.LoadHeld());
    }

    private static readonly string Big = string.Concat(Enumerable.Range(0, 30_000).Select(i => (i % 10007).ToString("D5")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_dropped_mid_body_is_retried_from_where_it_stopped(bool serverIgnoresRange)
    {
        _remote.AddFile("/test/big.bin", Big);
        var drops = 0;
        _remote.DownloadDrop = path => path.EndsWith("big.bin", StringComparison.Ordinal) && drops++ == 0 ? 40_000 : null;
        _remote.IgnoreRange = serverIgnoresRange;

        var (_, result) = await SyncAsync();

        Assert.True(result.AllDone, string.Join("; ", result.Results.Select(r => r.Message)));
        Assert.Equal(Big, File.ReadAllText(_local.Full("big.bin")));
        Assert.Equal([0L, 40_000L], _remote.GetOffsets);
        Assert.Empty(Directory.GetFiles(new StatePaths(_local.Path).TmpDir));
        await AssertUpToDateAsync();
    }

    [Fact]
    public async Task Download_part_from_an_earlier_run_is_resumed_and_stale_parts_are_cleaned()
    {
        _remote.AddFile("/test/big.bin", Big);
        var item = (await _remote.StatAsync("/test/big.bin", TestContext.Current.CancellationToken))!;
        var state = new StatePaths(_local.Path);
        var part = LocalFs.ResumablePartPath(state, "big.bin", item.Size, item.ETag!);
        File.WriteAllText(part, Big[..50_000]);
        var oldPart = LocalFs.ResumablePartPath(state, "gone.bin", 1, "x");
        File.WriteAllText(oldPart, "old");
        File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow - LocalFs.ResumableMaxAge - TimeSpan.FromHours(1));
        var opPart = LocalFs.NewTempPath(state, "someop");
        File.WriteAllText(opPart, "per-op");

        var (_, result) = await SyncAsync();

        Assert.True(result.AllDone);
        Assert.Equal(Big, File.ReadAllText(_local.Full("big.bin")));
        Assert.Equal([50_000L], _remote.GetOffsets);
        Assert.Empty(Directory.GetFiles(state.TmpDir));
    }

    [Fact]
    public void Schema_1_state_db_gets_the_new_held_table()
    {
        var path = Path.Combine(_local.Path, "old-state.db");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE held(key TEXT PRIMARY KEY, reason TEXT NOT NULL, held_at INTEGER NOT NULL);
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NULL);
                INSERT INTO meta VALUES('schema_version', '1');
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = StateStore.Open(path);
        store.Hold([new HeldItem("a.txt", OpType.Download, "a.txt", "remote new", "L - | R 1:x", DateTime.UtcNow)]);
        Assert.Equal(OpType.Download, Assert.Single(store.LoadHeld()).Type);
        Assert.Equal("2", store.GetMeta(MetaKeys.SchemaVersion));
    }

    private async Task<(CyclePlan Cycle, ExecutionResult Result)> SyncAsync(Dictionary<string, Side>? keepSide = null)
    {
        using var engine = SyncEngine.Open(_pair, _remote, NullLogger.Instance);
        var cycle = await engine.PlanAsync(keepSide ?? [], ct: TestContext.Current.CancellationToken);
        Assert.False(cycle.Guards.HasErrors, string.Join(" ", cycle.Guards.Errors));
        var result = await engine.ExecuteAsync(cycle, cycle.Plan.Ops, ct: TestContext.Current.CancellationToken);
        return (cycle, result);
    }

    private async Task AssertUpToDateAsync()
    {
        using var engine = SyncEngine.Open(_pair, _remote, NullLogger.Instance);
        var cycle = await engine.PlanAsync(ct: TestContext.Current.CancellationToken);
        Assert.True(cycle.Plan.Ops.Count == 0, "Expected no ops after sync:\n" + PlanAssert.Dump(cycle.Plan));
        Assert.True(engine.Store.InitialSyncDone);
    }

    private void AssertInSync()
    {
        var local = _local.Files().Where(f => f.Key != ".cloud.db").OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var remote = _remote.Files(Root).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(local, remote);
    }
}
