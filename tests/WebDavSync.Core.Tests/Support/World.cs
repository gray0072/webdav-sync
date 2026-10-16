using System.Text;
using WebDavSync.Core.Config;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;

namespace WebDavSync.Core.Tests.Support;

/// <summary>Builds planner inputs: local, remote and base trees described by paths and contents.</summary>
internal sealed class World
{
    public static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime T1 = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, LocalEntry> _l = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteEntry> _r = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaseEntry> _b = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unlistedL = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unlistedR = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedL = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedR = new(StringComparer.Ordinal);
    private ulong _nextId = 1000;

    public static ContentHash Hash(string content) => ContentHash.Compute(Encoding.UTF8.GetBytes(content));

    public static string ETag(string content) => "etag-" + Hash(content).Hex[..8];

    public World Local(string path, string content, DateTime? mtime = null, ulong? id = null, bool withHash = true)
    {
        var key = PathKey.FromPath(path);
        _l[key] = new LocalEntry(key, path, false, Encoding.UTF8.GetByteCount(content), mtime ?? T0, id ?? _nextId++, withHash ? Hash(content) : null);
        return this;
    }

    public World LocalDir(string path, ulong? id = null)
    {
        var key = PathKey.FromPath(path);
        _l[key] = new LocalEntry(key, path, true, 0, default, id ?? _nextId++);
        return this;
    }

    public World Remote(string path, string content, DateTime? mtime = null, string? etag = null, bool withHash = false)
    {
        var key = PathKey.FromPath(path);
        _r[key] = new RemoteEntry(key, path, false, Encoding.UTF8.GetByteCount(content), mtime ?? T0, etag ?? ETag(content), withHash ? Hash(content) : null);
        return this;
    }

    public World RemoteDir(string path)
    {
        var key = PathKey.FromPath(path);
        _r[key] = new RemoteEntry(key, path, true, 0, T0, null);
        return this;
    }

    public World Base(string path, string content, ulong? id = null, bool confirmed = true, string? remotePath = null)
    {
        var key = PathKey.FromPath(path);
        var size = Encoding.UTF8.GetByteCount(content);
        _b[key] = new BaseEntry(key, false, path, remotePath ?? path, id, size, T0, Hash(content), confirmed ? ETag(content) : null, size, T0, Hash(content), confirmed, T0);
        return this;
    }

    public World BaseDir(string path, ulong? id = null, string? remotePath = null)
    {
        var key = PathKey.FromPath(path);
        _b[key] = new BaseEntry(key, true, path, remotePath ?? path, id, 0, null, null, null, 0, T0, null, true, T0);
        return this;
    }

    /// <summary>File identical on both sides and in base.</summary>
    public World Synced(string path, string content, ulong? id = null)
    {
        var fileId = id ?? _nextId++;
        return Local(path, content, id: fileId).Remote(path, content).Base(path, content, fileId);
    }

    public World SyncedDir(string path, ulong? id = null)
    {
        var dirId = id ?? _nextId++;
        return LocalDir(path, dirId).RemoteDir(path).BaseDir(path, dirId);
    }

    public World RemoveLocal(string path)
    {
        _l.Remove(PathKey.FromPath(path));
        return this;
    }

    public World RemoveRemote(string path)
    {
        _r.Remove(PathKey.FromPath(path));
        return this;
    }

    public World UnlistedLocal(string folder)
    {
        _unlistedL.Add(PathKey.FromPath(folder));
        return this;
    }

    public World UnlistedRemote(string folder)
    {
        _unlistedR.Add(PathKey.FromPath(folder));
        return this;
    }

    public World SkippedLocal(string path)
    {
        _skippedL.Add(PathKey.FromPath(path));
        return this;
    }

    public World SkippedRemote(string path)
    {
        _skippedR.Add(PathKey.FromPath(path));
        return this;
    }

    public ulong? LocalId(string path) => _l[PathKey.FromPath(path)].FileId;

    public PlannerInput Input(PlannerSettings? settings = null)
    {
        var listedL = _l.Values.Where(e => e.IsDir).Select(e => e.Key).Append(string.Empty).Except(_unlistedL).ToHashSet(StringComparer.Ordinal);
        var listedR = _r.Values.Where(e => e.IsDir).Select(e => e.Key).Append(string.Empty).Except(_unlistedR).ToHashSet(StringComparer.Ordinal);
        return new PlannerInput(
            new LocalSnapshot(new Dictionary<string, LocalEntry>(_l), listedL, new HashSet<string>(_skippedL)),
            new RemoteSnapshot(new Dictionary<string, RemoteEntry>(_r), listedR, new HashSet<string>(_skippedR)),
            new Dictionary<string, BaseEntry>(_b),
            settings ?? Settings());
    }

    public Plan Plan(PlannerSettings? settings = null) => Planner.Create(Input(settings));

    public static PlannerSettings Settings(bool union = false, ConflictWinner winner = ConflictWinner.Local) => new()
    {
        UnionMode = union,
        ConflictWinner = winner,
        Now = new DateTime(2026, 3, 4, 5, 6, 7),
        MachineName = "PC",
    };
}

internal static class PlanAssert
{
    public static SyncOp Single(Plan plan, OpType type, string key)
    {
        var matches = plan.Ops.Where(o => o.Type == type && o.Key == PathKey.FromPath(key)).ToList();
        Assert.True(matches.Count == 1, $"Expected one {type} for '{key}', plan:\n{Dump(plan)}");
        return matches[0];
    }

    public static void Only(Plan plan, params (OpType Type, string Key)[] expected)
    {
        var actual = plan.Ops.Select(o => (o.Type, o.Key)).OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Type).ToList();
        var wanted = expected.Select(e => (e.Type, PathKey.FromPath(e.Key))).OrderBy(x => x.Item2, StringComparer.Ordinal).ThenBy(x => x.Type).ToList();
        Assert.True(actual.SequenceEqual(wanted), $"Expected:\n{string.Join("\n", wanted)}\nActual plan:\n{Dump(plan)}");
    }

    public static void Empty(Plan plan) => Assert.True(plan.Ops.Count == 0, "Expected an empty plan:\n" + Dump(plan));

    public static void NoDeletes(Plan plan) =>
        Assert.False(plan.Ops.Any(o => o.IsDelete), "Expected no deletes:\n" + Dump(plan));

    public static string Dump(Plan plan) => string.Join("\n", plan.Ops.Select(o => "  " + o));
}
