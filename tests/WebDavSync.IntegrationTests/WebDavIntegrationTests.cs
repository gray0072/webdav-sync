using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Execution;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;

namespace WebDavSync.IntegrationTests;

/// <summary>Client, probe and scenarios A, B, C (§7) against a real WebDAV server.</summary>
[Collection(RcloneCollection.Name)]
public sealed class WebDavIntegrationTests(RcloneWebDavFixture server) : IDisposable
{
    private readonly string _localRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wds-it", Guid.NewGuid().ToString("N")[..10])).FullName;
    private readonly string _remoteRoot = "/webdavsync-test-" + Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_localRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Probe_reports_capabilities()
    {
        Assert.SkipUnless(server.Available, server.SkipReason);
        using var client = server.CreateClient();
        var report = await new WebDavProbe(client, NullLogger.Instance).RunAsync(server.Endpoint.ToString(), RcloneWebDavFixture.User, "/", Ct);

        Assert.Equal(true, report.Capabilities["AuthOk"]);
        Assert.Equal(true, report.Capabilities["Depth1"]);
        Assert.Equal(true, report.Capabilities["MoveFolder"]);
        Assert.Equal(true, report.Capabilities["Delete"]);
        Assert.Contains("CaseInsensitive", report.Capabilities.Keys);
        Assert.DoesNotContain((await client.ListAsync("/", Ct)).Children, c => c.Name.StartsWith(".webdavsync-probe-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edge25_Wrong_password_is_an_auth_error()
    {
        Assert.SkipUnless(server.Available, server.SkipReason);
        using var client = server.CreateClient("wrong");
        var ex = await Assert.ThrowsAsync<RemoteException>(() => client.StatAsync("/", Ct));
        Assert.Equal(RemoteErrorKind.Auth, ex.Kind);
    }

    [Fact]
    public async Task Edge6_Special_characters_round_trip()
    {
        Assert.SkipUnless(server.Available, server.SkipReason);
        using var client = server.CreateClient();
        await client.MkdirAsync(_remoteRoot, Ct);
        const string name = "Отчёт #1 + 50% & co.txt";
        var bytes = Encoding.UTF8.GetBytes("payload");
        await client.PutAsync($"{_remoteRoot}/{name}", new MemoryStream(bytes), bytes.Length, (DateTime?)null, Ct);

        var listing = await client.ListAsync(_remoteRoot, Ct);
        Assert.True(listing.Complete);
        var item = Assert.Single(listing.Children);
        Assert.Equal(name, item.Name);
        Assert.Equal(bytes.Length, item.Size);

        await using var download = await client.GetAsync($"{_remoteRoot}/{name}", 0, Ct);
        using var reader = new StreamReader(download.Content);
        Assert.Equal("payload", await reader.ReadToEndAsync(Ct));
        await client.DeleteAsync(_remoteRoot, Ct);
    }

    [Fact]
    public async Task Scenario_A_cloud_to_empty_local_folder()
    {
        Assert.SkipUnless(server.Available, server.SkipReason);
        using var client = server.CreateClient();
        await client.MkdirAsync(_remoteRoot, Ct);
        await client.MkdirAsync($"{_remoteRoot}/Docs", Ct);
        await PutAsync(client, $"{_remoteRoot}/Docs/Файл.txt", "remote content");
        await PutAsync(client, $"{_remoteRoot}/top.txt", "top");

        var pair = CreatePair();
        await SyncAsync(pair, client);

        Assert.Equal("remote content", File.ReadAllText(Path.Combine(_localRoot, "Docs", "Файл.txt")));
        Assert.Equal("top", File.ReadAllText(Path.Combine(_localRoot, "top.txt")));
        await AssertUpToDateAsync(pair, client);
    }

    [Fact]
    public async Task Scenario_B_union_merge()
    {
        Assert.SkipUnless(server.Available, server.SkipReason);
        using var client = server.CreateClient();
        await client.MkdirAsync(_remoteRoot, Ct);
        await PutAsync(client, $"{_remoteRoot}/same.txt", "identical");
        await PutAsync(client, $"{_remoteRoot}/diff.txt", "remote");
        File.WriteAllText(Path.Combine(_localRoot, "same.txt"), "identical");
        File.WriteAllText(Path.Combine(_localRoot, "diff.txt"), "local, longer");

        var pair = CreatePair();
        var (cycle, _) = await SyncAsync(pair, client);

        Assert.DoesNotContain(cycle.Plan.Ops, o => o.IsDelete);
        var names = (await client.ListAsync(_remoteRoot, Ct)).Children.Select(c => c.Name).Where(n => n != ".webdavsync").Order().ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains(names, n => n.StartsWith("diff (conflict ", StringComparison.Ordinal));
        Assert.Equal(3, Directory.GetFiles(_localRoot).Length);
        await AssertUpToDateAsync(pair, client);
    }

    [Fact]
    public async Task Scenario_C_and_normal_cycles()
    {
        Assert.SkipUnless(server.Available, server.SkipReason);
        using var client = server.CreateClient();
        Directory.CreateDirectory(Path.Combine(_localRoot, "Folder", "Sub"));
        File.WriteAllText(Path.Combine(_localRoot, "Folder", "Sub", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_localRoot, "b.txt"), "b");
        File.WriteAllText(Path.Combine(_localRoot, "empty.txt"), string.Empty);

        var pair = CreatePair();
        await SyncAsync(pair, client);
        Assert.NotNull(await client.StatAsync($"{_remoteRoot}/Folder/Sub/a.txt", Ct));
        Assert.NotNull(await client.StatAsync($"{_remoteRoot}/.webdavsync/pair.json", Ct));
        await AssertUpToDateAsync(pair, client);

        // Local: rename folder, edit, delete. Remote: new file.
        Directory.Move(Path.Combine(_localRoot, "Folder"), Path.Combine(_localRoot, "Renamed"));
        File.WriteAllText(Path.Combine(_localRoot, "b.txt"), "b edited");
        File.Delete(Path.Combine(_localRoot, "empty.txt"));
        await PutAsync(client, $"{_remoteRoot}/from-cloud.txt", "cloud");

        var (cycle, _) = await SyncAsync(pair, client);
        Assert.Equal(1, cycle.Plan.Count(OpType.MoveRemote));
        Assert.NotNull(await client.StatAsync($"{_remoteRoot}/Renamed/Sub/a.txt", Ct));
        Assert.Null(await client.StatAsync($"{_remoteRoot}/Folder", Ct));
        Assert.Null(await client.StatAsync($"{_remoteRoot}/empty.txt", Ct));
        Assert.Equal("cloud", File.ReadAllText(Path.Combine(_localRoot, "from-cloud.txt")));
        await AssertUpToDateAsync(pair, client);

        // Remote rename of a folder becomes a local move.
        await client.MoveAsync($"{_remoteRoot}/Renamed", $"{_remoteRoot}/Moved", false, Ct);
        (cycle, _) = await SyncAsync(pair, client);
        Assert.Equal([OpType.MoveLocal], cycle.Plan.ActionOps.Select(o => o.Type));
        Assert.True(File.Exists(Path.Combine(_localRoot, "Moved", "Sub", "a.txt")));
        await AssertUpToDateAsync(pair, client);
    }

    private PairSettings CreatePair()
    {
        var pair = new PairSettings
        {
            Name = "it",
            LocalRoot = _localRoot,
            RemoteUrl = server.Endpoint.ToString(),
            RemoteRoot = _remoteRoot,
            User = RcloneWebDavFixture.User,
            LocalDeleteTarget = LocalDeleteTarget.StateTrash,
            MassDeleteCount = 1000,
            MassDeletePercent = 100,
        };
        PairSetup.InitializeLocal(pair, resetState: false);
        return pair;
    }

    private static async Task<(CyclePlan Cycle, ExecutionResult Result)> SyncAsync(PairSettings pair, IRemoteStore client)
    {
        using var engine = SyncEngine.Open(pair, client, NullLogger.Instance);
        var cycle = await engine.PlanAsync(ct: Ct);
        var result = await engine.ExecuteAsync(cycle, cycle.Plan.Ops, ct: Ct);
        Assert.True(result.AllDone, string.Join("\n", result.Results.Where(r => r.Status != Core.State.JournalStatus.Done).Select(r => $"{r.Op}: {r.Status} {r.Message}")) + result.AbortReason);
        return (cycle, result);
    }

    private static async Task AssertUpToDateAsync(PairSettings pair, IRemoteStore client)
    {
        using var engine = SyncEngine.Open(pair, client, NullLogger.Instance);
        var cycle = await engine.PlanAsync(ct: Ct);
        Assert.True(cycle.Plan.Ops.Count == 0, "Expected an empty plan:\n" + string.Join("\n", cycle.Plan.Ops));
        Assert.True(engine.Store.InitialSyncDone);
    }

    private static Task PutAsync(WebDavClient client, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return client.PutAsync(path, new MemoryStream(bytes), bytes.Length, (DateTime?)null, Ct);
    }
}
